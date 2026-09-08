using Compiler.Desugaring;
using Compiler.Tokenizer;
using Compiler.Desugaring.Passes;
using Compiler.Targeting;
using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;
using Compiler.Verification;

namespace Compiler.Instantiation;

/// <summary>
/// Generates real AST bodies for <c>IsSynthesized = true</c> stub routines on record, entity,
/// and crashable types. Runs as a global pass after all per-file desugaring (Phase 6a).
///
/// <para>Generated bodies (keyed by <c>RoutineInfo.RegistryKey</c> ??<c>ctx.VariantBodies</c>):</para>
/// <list type="bullet">
///   <item><c>eq</c>   -> field-by-field <c>==</c> AND-chain for concrete <see cref="RecordTypeInfo"/>, <see cref="EntityTypeInfo"/>, <see cref="TupleTypeInfo"/>.</item>
///   <item><c>hash</c> -> XOR-chain of <c>me.f.hash()</c> calls for records, entities, tuples.</item>
///   <item><c>represent</c> / <c>diagnose</c> -> f-string body for <see cref="RecordTypeInfo"/> and
///         <see cref="EntityTypeInfo"/>, including generic definitions (monomorphization substitutes type params).</item>
///   <item><c>represent</c> on crashable ??<c>return me.crash_message()</c>.</item>
///   <item><c>diagnose</c> on crashable -> f-string <c>Module.Name(crash_message, field: val, ...)</c>.</item>
///   <item><c>Text.create(from: T)</c> ??<c>return from.represent()</c>.</item>
/// </list>
///
/// <para>Not generated here:</para>
/// <list type="bullet">
///   <item><see cref="VariantTypeInfo"/> bodies — pattern dispatch on numeric value; not
///         expressible in plain AST. Emitted by <c>ErrorHandlingVariantPass</c>.</item>
///   <item>Records with <c>HasDirectBackendType</c> — intrinsic types with no RF member
///         variables (skipped early in <see cref="HandleRecord"/>).</item>
///   <item><c>Maybe[T].represent</c> / <c>diagnose</c> — defined explicitly in
///         <c>Core/Errors/Maybe.rf</c> (treated as user code, not synthesized).</item>
/// </list>
/// </summary>
public sealed class WiredRoutinePass(DesugaringContext ctx)
{
    private const string RepresentMemberRoutineName = Compiler.Declaration.RuntimeContract.Display.Represent;
    private const string DiagnoseMemberRoutineName = Compiler.Declaration.RuntimeContract.Display.Diagnose;
    private const string SerializeMemberRoutineName = Compiler.Declaration.RuntimeContract.Serialize;
    private const string HashMemberRoutineName = "hash";
    private const string CreateMemberRoutineName = "create";
    private const string BitXorMemberRoutineName = "bitxor";
    private const string ResultVarName = "result";
    private const string FirstVarName = "first";
    private const string OtherParamName = "other";

    private static readonly SourceLocation _synthLoc = new(FileName: "",
        Line: 0,
        Column: 0,
        Position: 0);

    /// <summary>Synthesizes and registers all wired routines for the current program.</summary>
    public void RunGlobal()
    {
        TypeInfo? textType = ctx.Registry.LookupType(name: "Text");
        TypeInfo? boolType = ctx.Registry.LookupType(name: "Bool");
        TypeInfo? u64Type = ctx.Registry.LookupType(name: "U64");
        TypeInfo? s32Type = ctx.Registry.LookupType(name: "S32");
        TypeInfo? s64Type = ctx.Registry.LookupType(name: "S64");
        TypeInfo? byteSizeType = ctx.Registry.LookupType(name: "ByteSize");
        TypeInfo? logicBreachedErrorType = ctx.Registry.LookupType(name: "LogicBreachedError");
        // TypeKind lives in `module BuilderQuery` — qualify (a bare lookup depended on the short-name scan).
        TypeInfo? typeKindType = ctx.Registry.LookupType(name: "BuilderQuery.TypeKind");
        TypeInfo? listTypeDef = ctx.Registry.LookupType(name: "List");
        TypeInfo? listTextType = listTypeDef != null && textType != null
            ? ctx.Registry.GetOrCreateResolution(genericDef: listTypeDef,
                typeArguments: [textType])
            : null;
        if (textType == null || boolType == null)
            return;

        // Base build: build derive bodies for ALL concrete types, not just live ones (so the precompiled
        // stdlib base defines every routine it references). Normal builds keep the liveness filter.
        foreach (RoutineInfo routine in ctx.Registry.GetAllRoutines(requireLive: !ctx.SynthesizeAllDerives))
        {
            if (!routine.IsSynthesized) continue;
            if (ctx.RoutineBodies.ContainsKey(key: routine.RegistryKey)) continue;
            if (ctx.VariantBodies.ContainsKey(key: routine.RegistryKey)) continue;

            // Auto-generated variant arm constructors (handled before the by-NAME explicit-impl skip
            // below): an extractor `Arm.create(from: V)` shares the name `create` with the arm type's
            // other constructors, so a name-only skip would wrongly drop it. This hook is overload-precise
            // (owner/param must be in an arm relationship), so it is safe to run first.
            if (TryBuildVariantArmConstructorBody(routine: routine,
                    body: out Statement? armCtorBody))
            {
                ctx.VariantBodies[key: routine.RegistryKey] = armCtorBody!;
                continue;
            }

            // Skip if an explicit (non-synthesized) implementation already exists in the registry.
            // This prevents synthesized bodies from overriding custom stdlib implementations
            // such as Witnessed[T,P].represent / diagnose defined in Witnessed.rf.
            if (routine.OwnerType != null && ctx.Registry
                                                .GetMemberRoutinesForType(type: routine.OwnerType)
                                                .Any(r => r.Name == routine.Name &&
                                                          !r.IsSynthesized))
                continue;

            // BuilderQuery constant routines apply to all owner types -> check by name first.
            if (routine.OwnerType != null && TryHandleBuilderQueryConstant(routine: routine,
                    textType: textType,
                    u64Type: u64Type,
                    s64Type: s64Type,
                    boolType: boolType,
                    typeKindType: typeKindType,
                    listTextType: listTextType,
                    byteSizeType: byteSizeType))
                continue;

            // Standalone BuilderQuery constants (no owner type): page_size, target_os, etc.
            if (routine.OwnerType == null && TryHandleStandaloneBuilderQueryConstant(
                    routine: routine,
                    textType: textType,
                    u64Type: u64Type,
                    byteSizeType: byteSizeType))
                continue;

            // Cycle-collector per-type hooks + unified destructor + owner-type dispatch.
            if (TrySynthesizeHookOrDispatch(routine: routine,
                    textType: textType,
                    boolType: boolType,
                    u64Type: u64Type,
                    s32Type: s32Type,
                    s64Type: s64Type,
                    logicBreachedErrorType: logicBreachedErrorType,
                    listTypeDef: listTypeDef))
                continue;
        }

        // Tuple types appear only as local variable / expression types and never as routine
        // signatures, so TypeLivenessPass cannot seed them — `GetAllRoutines()` filters them
        // out via `IsConcreteTypeLive`. Iterate them directly from `_resolutions` (via
        // `GetTypesWithMemberRoutines`) so their wired bodies are synthesized regardless of liveness.
        foreach (TypeInfo type in ctx.Registry.GetTypesWithMemberRoutines())
        {
            if (type is not TupleTypeInfo tuple) continue;
            foreach (RoutineInfo routine in ctx.Registry.GetMemberRoutinesForType(type))
            {
                if (!routine.IsSynthesized) continue;
                if (ctx.RoutineBodies.ContainsKey(key: routine.RegistryKey)) continue;
                if (ctx.VariantBodies.ContainsKey(key: routine.RegistryKey)) continue;
                HandleTuple(routine: routine,
                    tuple: tuple,
                    textType: textType,
                    s32Type: s32Type);
            }
        }

        // Routine types are structural (never a declared owner), so — like tuples — they never reach the
        // GetAllRoutines dispatch above; iterate them from the resolutions cache. A routine VALUE serializes
        // by boxing its `represent()` signature Text (the zero-field BuildSerializeBody path) — the universal
        // serialize walk (now unconditional) over a routine-typed member calls it. represent/diagnose come
        // from the `is RoutineType` DeriveText overrides (simple `type_name()`, no constructor to re-resolve).
        foreach (TypeInfo type in ctx.Registry.GetResolvedRoutineTypes())
        {
            foreach (RoutineInfo routine in ctx.Registry.GetMemberRoutinesForType(type))
            {
                if (routine.Name != SerializeMemberRoutineName) continue;
                if (ctx.RoutineBodies.ContainsKey(key: routine.RegistryKey)) continue;
                if (ctx.VariantBodies.ContainsKey(key: routine.RegistryKey)) continue;
                Statement? body = BuildSerializeBody(owner: type, fields: [], textType: textType);
                if (body != null) ctx.VariantBodies[key: routine.RegistryKey] = body;
            }
        }


        // GetAllRoutines() filters out generic-definition owner types to prevent T/K,V placeholders
        // in LLVM. However, BuilderQuery routines on generic defs return only fixed literals or
        // empty collections — they never reference the generic parameters. GMP needs these bodies
        // to emit the generic-def LLVM function (e.g. @Collections.BTreeDictNode.member_variable_count)
        // so that wrapper forwarders for Hijacked[BTreeDictNode] have a valid callee.
        RunForGenericDefBuilderQueryRoutines(textType: textType,
            u64Type: u64Type,
            s64Type: s64Type,
            boolType: boolType,
            typeKindType: typeKindType,
            listTextType: listTextType,
            byteSizeType: byteSizeType);

        // Synthesize wired routines (eq, hash, represent, diagnose) for
        // generic def entity/record types that have no source-defined implementation.
        // GMP needs a body in VariantBodies[genericDefKey] to rewrite into concrete instances.
        RunForGenericDefWiredRoutines(textType: textType, boolType: boolType, s32Type: s32Type);
    }

    /// <summary>
    /// Handles the cycle-collector per-type hooks (<c>roam_trace_impl</c>/<c>roam_free_impl</c>), the
    /// unified <c>destroy</c> destructor, and — for anything else — the owner-type dispatch to the
    /// per-kind <c>HandleX</c> synthesizers. Always returns <c>true</c> (the caller should advance to
    /// the next routine).
    /// </summary>
    private bool TrySynthesizeHookOrDispatch(RoutineInfo routine, TypeInfo textType, TypeInfo boolType,
        TypeInfo? u64Type, TypeInfo? s32Type, TypeInfo? s64Type, TypeInfo? logicBreachedErrorType,
        TypeInfo? listTypeDef)
    {
        switch (routine)
        {
            // Cycle-collector per-type hooks (see AutoWiredRegistrationPass.MaybeRegisterRoamHook).
            case { Name: "roam_trace_impl", Parameters.Count: 0 }:
                ctx.VariantBodies[key: routine.RegistryKey] =
                    BuildRoamTraceBody(owner: routine.OwnerType);
                return true;
            case { Name: "roam_free_impl", Parameters.Count: 0 }:
                ctx.VariantBodies[key: routine.RegistryKey] =
                    BuildRoamFreeBody(owner: routine.OwnerType);
                return true;
            // Unified destructor: clone the `@overridable T.destroy()` derive (the `expand allmemvarof`
            // field-walk from DeriveText, kind-specialized for entity/variant). Applies to every concrete
            // type; the per-type body comes entirely from the template.
            // A routine VALUE is a trivially-destructible leaf (a fat pointer over non-owned code/env); its
            // `destroy` (registered in GetOrCreateRoutineType so a record/entity field-walk over a routine-typed
            // field resolves + links) is a bare return — there is no universal derive template to clone for it.
            case { Name: "destroy", Parameters.Count: 0 } when routine.OwnerType is RoutineTypeInfo:
                ctx.VariantBodies[key: routine.RegistryKey] =
                    new BlockStatement(Statements: [new ReturnStatement(Value: null, Location: _synthLoc)],
                        Location: _synthLoc);
                return true;
            case { Name: "destroy", Parameters.Count: 0 } when routine.OwnerType is { } destroyOwner:
                ctx.VariantBodies[key: routine.RegistryKey] =
                    CloneUniversalDeriveBody(ownerType: destroyOwner, synthesized: routine,
                        memberRoutineName: "destroy")
                    ?? throw new System.InvalidOperationException(
                        message: $"destroy derive could not be cloned for '{destroyOwner.FullName}'.");
                return true;
            // Derived comparison operators (lt/le/gt/ge) delegate to the type's own `cmp` — the DeriveText
            // templates spell `return me.cmp(you) == ME_SMALL` etc. Like `destroy`, they apply to EVERY
            // concrete type obeying Comparable regardless of kind (numeric scalar, record, choice), so clone
            // the per-name template here rather than duplicating a case in each per-kind HandleX. The retired
            // DerivedOperatorPass used to generate these; the everywhere-derive registers the stub (guarded on
            // a template existing — AutoWiredRegistrationPass), and this materializes its body so the demand
            // collector and codegen see a definition (else `a < b` links to an undefined `<Type>.lt`).
            case { Name: "lt" or "le" or "gt" or "ge", Parameters.Count: 1 } when routine.OwnerType is { } cmpOwner
                    && ctx.Registry.GetDeriveTemplate(name: routine.Name, arity: 1, forType: cmpOwner) is not null:
                ctx.VariantBodies[key: routine.RegistryKey] =
                    CloneUniversalDeriveBody(ownerType: cmpOwner, synthesized: routine,
                        memberRoutineName: routine.Name)
                    ?? throw new System.InvalidOperationException(
                        message: $"{routine.Name} derive could not be cloned for '{cmpOwner.FullName}'.");
                return true;
            default:
                DispatchByOwnerType(routine: routine,
                    textType: textType,
                    boolType: boolType,
                    u64Type: u64Type,
                    s32Type: s32Type,
                    s64Type: s64Type,
                    logicBreachedErrorType: logicBreachedErrorType,
                    listTypeDef: listTypeDef);
                return true;
        }
    }

    /// <summary>
    /// Dispatches a synthesized routine to the appropriate per-kind <c>HandleX</c> synthesizer based
    /// on the concrete owner type (tuple/choice/flags/variant/record/crashable/entity/routine-value).
    /// </summary>
    private void DispatchByOwnerType(RoutineInfo routine, TypeInfo textType, TypeInfo boolType,
        TypeInfo? u64Type, TypeInfo? s32Type, TypeInfo? s64Type, TypeInfo? logicBreachedErrorType,
        TypeInfo? listTypeDef)
    {
        switch (routine.OwnerType)
        {
            case TupleTypeInfo tuple:
                HandleTuple(routine: routine,
                    tuple: tuple,
                    textType: textType,
                    s32Type: s32Type);
                break;

            case ChoiceTypeInfo choice:
                HandleChoice(routine: routine,
                    choice: choice,
                    textType: textType,
                    boolType: boolType,
                    logicBreachedErrorType: logicBreachedErrorType,
                    u64Type: u64Type,
                    s64Type: s64Type,
                    listTypeDef: listTypeDef);
                break;

            case FlagsTypeInfo flags:
                HandleFlags(routine: routine,
                    flags: flags,
                    textType: textType,
                    boolType: boolType,
                    u64Type: u64Type,
                    listTypeDef: listTypeDef);
                break;

            case VariantTypeInfo variant:
                HandleVariant(routine: routine, variant: variant, textType: textType);
                break;

            case RecordTypeInfo record:
                HandleRecord(routine: routine,
                    record: record,
                    textType: textType,
                    boolType: boolType,
                    s32Type: s32Type);
                break;

            case CrashableTypeInfo crashable:
                HandleCrashable(routine: routine, crashable: crashable, textType: textType);
                break;

            case EntityTypeInfo entity:
                HandleEntity(routine: routine,
                    entity: entity,
                    textType: textType,
                    boolType: boolType);
                break;

            case RoutineTypeInfo routineOwner
                when routine.Name == SerializeMemberRoutineName && !routineOwner.IsGenericDefinition:
                // A routine VALUE boxes its `represent()` signature Text as its serialize (the
                // zero-field path) — a resolved CreatorExpression, unlike the RF template's
                // `SerialValue(...)` which doesn't re-resolve when cloned for a structural type.
                ctx.VariantBodies[key: routine.RegistryKey] =
                    BuildSerializeBody(owner: routineOwner, fields: [], textType: textType);
                break;
        }
    }

    private void RunForGenericDefBuilderQueryRoutines(TypeInfo textType, TypeInfo? u64Type,
        TypeInfo? s64Type, TypeInfo? boolType, TypeInfo? typeKindType,
        TypeInfo? listTextType, TypeInfo? byteSizeType)
    {
        foreach (TypeInfo type in ctx.Registry.GetTypesWithMemberRoutines())
        {
            if (!type.IsGenericDefinition) continue;
            foreach (RoutineInfo routine in ctx.Registry.GetMemberRoutinesForType(type))
            {
                if (!routine.IsSynthesized) continue;
                if (!BuilderInfoProvider.IsBuilderQueryRoutine(name: routine.Name)) continue;
                if (ctx.VariantBodies.ContainsKey(key: routine.RegistryKey)) continue;
                TryHandleBuilderQueryConstant(routine: routine,
                    textType: textType,
                    u64Type: u64Type,
                    s64Type: s64Type,
                    boolType: boolType,
                    typeKindType: typeKindType,
                    listTextType: listTextType,
                    byteSizeType: byteSizeType);
            }
        }
    }

    private void RunForGenericDefWiredRoutines(TypeInfo textType, TypeInfo boolType,
        TypeInfo? s32Type)
    {
        TypeInfo? u64Type = ctx.Registry.LookupType(name: "U64");
        foreach (TypeInfo type in ctx.Registry.GetTypesWithMemberRoutines())
        {
            if (!type.IsGenericDefinition) continue;
            // Skip if a non-synthesized override exists — source-defined wins.
            var memberRoutines = ctx.Registry
                             .GetMemberRoutinesForType(type)
                             .ToList();
            foreach (RoutineInfo routine in memberRoutines)
            {
                if (!routine.IsSynthesized) continue;
                if (ctx.VariantBodies.ContainsKey(key: routine.RegistryKey)) continue;
                if (memberRoutines.Any(r => r.Name == routine.Name && !r.IsSynthesized)) continue;

                // Unified destructor for generic-def entity/record types (e.g. ListEmitter[T],
                // DictEntry[K,V]). The first loop's destroy handler runs over GetAllRoutines(),
                // which excludes generic-def owners, so without this their destroy body is never
                // synthesized — and GMP.BuildBody returns null for a synthesized memberRoutine with no
                // VariantBody, leaving scope-exit `emitter.destroy()` calls (inserted once these
                // helper locals exist, e.g. from for-loop iteration) undefined at link.
                if (routine is { Name: "destroy", Parameters.Count: 0 })
                {
                    ctx.VariantBodies[key: routine.RegistryKey] =
                        CloneUniversalDeriveBody(ownerType: routine.OwnerType!, synthesized: routine,
                            memberRoutineName: "destroy")
                        ?? throw new System.InvalidOperationException(
                            message: $"destroy derive could not be cloned for '{routine.OwnerType?.FullName}'.");
                    continue;
                }

                if (routine is { Name: "roam_trace_impl", Parameters.Count: 0 })
                {
                    ctx.VariantBodies[key: routine.RegistryKey] =
                        BuildRoamTraceBody(owner: routine.OwnerType);
                    continue;
                }

                if (routine is { Name: "roam_free_impl", Parameters.Count: 0 })
                {
                    ctx.VariantBodies[key: routine.RegistryKey] =
                        BuildRoamFreeBody(owner: routine.OwnerType);
                    continue;
                }

                // Derived comparison operators on a generic DEF (e.g. Array[T, N].lt) — clone the DeriveText
                // template body here so GMP/the collector has a def body to monomorphize onto each concrete
                // instance. Mirrors the concrete-type case in TrySynthesizeHookOrDispatch; the retired
                // DerivedOperatorPass previously produced these.
                if (routine is { Name: "lt" or "le" or "gt" or "ge", Parameters.Count: 1 }
                    && routine.OwnerType is { } cmpDefOwner
                    && ctx.Registry.GetDeriveTemplate(name: routine.Name, arity: 1, forType: cmpDefOwner) is not null)
                {
                    ctx.VariantBodies[key: routine.RegistryKey] =
                        CloneUniversalDeriveBody(ownerType: cmpDefOwner, synthesized: routine,
                            memberRoutineName: routine.Name)
                        ?? throw new System.InvalidOperationException(
                            message: $"{routine.Name} derive could not be cloned for '{cmpDefOwner.FullName}'.");
                    continue;
                }

                switch (type)
                {
                    case EntityTypeInfo entity:
                        HandleEntityGenericDefWired(routine: routine,
                            entity: entity,
                            textType: textType,
                            boolType: boolType,
                            u64Type: u64Type);
                        break;
                    case RecordTypeInfo { BackendType: null } record:
                        HandleRecordGenericDefWired(routine: routine,
                            record: record,
                            textType: textType,
                            boolType: boolType,
                            s32Type: s32Type,
                            u64Type: u64Type);
                        break;
                }
            }
        }
    }

    private void HandleEntityGenericDefWired(RoutineInfo routine, EntityTypeInfo entity,
        TypeInfo textType, TypeInfo boolType, TypeInfo? u64Type)
    {
        switch (routine.Name)
        {
            case RepresentMemberRoutineName:
                ctx.VariantBodies[key: routine.RegistryKey] =
                    CloneUniversalDeriveBody(ownerType: entity, synthesized: routine,
                        memberRoutineName: RepresentMemberRoutineName)
                    ?? BuildTextBody(ownerType: entity, fields: entity.MemberVariables,
                        textType: textType, diagnose: false);
                break;
            case DiagnoseMemberRoutineName:
                ctx.VariantBodies[key: routine.RegistryKey] =
                    CloneUniversalDeriveBody(ownerType: entity, synthesized: routine,
                        memberRoutineName: DiagnoseMemberRoutineName)
                    ?? BuildTextBody(ownerType: entity, fields: entity.MemberVariables,
                        textType: textType, diagnose: true);
                break;
            case "eq":
                ctx.VariantBodies[key: routine.RegistryKey] = entity.MemberVariables.Count == 0
                    ? BuildReturnTrueBody(boolType: boolType)
                    : BuildEqBody(ownerType: entity,
                        fields: entity.MemberVariables,
                        boolType: boolType);
                break;
            case HashMemberRoutineName when entity.MemberVariables.Count > 0 && u64Type != null &&
                                     routine.Parameters.Count == 0:
                ctx.VariantBodies[key: routine.RegistryKey] = BuildHashBody(ownerType: entity,
                    fields: entity.MemberVariables,
                    u64Type: u64Type);
                break;
            case HashMemberRoutineName when entity.MemberVariables.Count > 0 && u64Type != null &&
                                     routine.Parameters.Count == 2:
                ctx.VariantBodies[key: routine.RegistryKey] =
                    BuildSecureHashBody(ownerType: entity,
                        fields: entity.MemberVariables,
                        u64Type: u64Type);
                break;
        }
    }

    private void HandleRecordGenericDefWired(RoutineInfo routine, RecordTypeInfo record,
        TypeInfo textType, TypeInfo boolType, TypeInfo? s32Type,
        TypeInfo? u64Type)
    {
        switch (routine.Name)
        {
            case RepresentMemberRoutineName:
                ctx.VariantBodies[key: routine.RegistryKey] = BuildTextBody(ownerType: record,
                    fields: record.MemberVariables,
                    textType: textType,
                    diagnose: false);
                break;
            case DiagnoseMemberRoutineName:
                ctx.VariantBodies[key: routine.RegistryKey] = BuildTextBody(ownerType: record,
                    fields: record.MemberVariables,
                    textType: textType,
                    diagnose: true);
                break;
            case "eq":
                ctx.VariantBodies[key: routine.RegistryKey] = BuildEqBody(ownerType: record,
                    fields: record.MemberVariables,
                    boolType: boolType);
                break;
            case HashMemberRoutineName when u64Type != null && routine.Parameters.Count == 0:
                ctx.VariantBodies[key: routine.RegistryKey] = BuildHashBody(ownerType: record,
                    fields: record.MemberVariables,
                    u64Type: u64Type);
                break;
            case HashMemberRoutineName when u64Type != null && routine.Parameters.Count == 2:
                ctx.VariantBodies[key: routine.RegistryKey] =
                    BuildSecureHashBody(ownerType: record,
                        fields: record.MemberVariables,
                        u64Type: u64Type);
                break;
            case "cmp" when s32Type != null:
                ctx.VariantBodies[key: routine.RegistryKey] = BuildCmpBody(ownerType: record,
                    fields: record.MemberVariables,
                    s32Type: s32Type,
                    boolType: boolType);
                break;
            // `assign` (shallow store) / `copy` (deep) on a GENERIC-DEFINITION record: the field-walk
            // `BuildRecordCopyBody` (same body `HandleRecord` uses for a generic def — CloneUniversalDeriveBody
            // is null for a def). Registering the DEF body here is REQUIRED so GMP can monomorphize it onto
            // each concrete instance (`Maybe[S32].assign`); without it an auto-derived carrier assign/copy was
            // declared+called but never defined (over-prune). Mirrors how eq/cmp/hash/represent above emit.
            case "assign":
            case "duplicate":
                ctx.VariantBodies[key: routine.RegistryKey] = BuildRecordCopyBody(record: record);
                break;
        }
    }

    //  Per-type handlers

    private void HandleRecord(RoutineInfo routine, RecordTypeInfo record, TypeInfo textType,
        TypeInfo boolType, TypeInfo? s32Type)
    {
        // Numeric create bodies for @llvm-typed primitive records.
        // S64.create(from: Choice) -> sign_extend; U64.create(from: Flags) -> reinterpret_bits.
        // Must be checked before the HasDirectBackendType guard because these live on S64/U64.
        if (TryHandleNumericCreate(routine: routine, record: record))
            return;

        // `store` / `clone` bodies are field-independent (`return me` / `return me.assign()`), so
        // synthesize them BEFORE the opaque-backend skip — @llvm primitives (S64, Bool, …) need real
        // (trivial, LLVM-inlined) bodies so explicit `clone()`/`store()` calls link. Only synth stubs
        // reach here; user-written copies (e.g. Text.store, which retains) keep their own body.
        if (TryHandleRecordCopyOrSerialize(routine: routine, record: record, textType: textType))
            return;

        if (record.BackendType != null) return;

        HandleRecordFieldWiredRoutine(routine: routine, record: record, textType: textType,
            boolType: boolType, s32Type: s32Type);
    }

    /// <summary>
    /// Numeric create bodies for @llvm-typed primitive records.
    /// <c>S64.create(from: Choice)</c> → sign_extend; <c>U64.create(from: Flags)</c> → reinterpret_bits.
    /// Must be checked before the HasDirectBackendType guard because these live on S64/U64.
    /// Returns <c>true</c> if a body was synthesized.
    /// </summary>
    private bool TryHandleNumericCreate(RoutineInfo routine, RecordTypeInfo record)
    {
        if (routine is { Name: "create", Parameters.Count: 1 })
        {
            TypeInfo paramType = routine.Parameters[index: 0].Type;
            string paramName = routine.Parameters[index: 0].Name;
            TypeInfo? s64Type = ctx.Registry.LookupType(name: "S64");
            TypeInfo? u64Type = ctx.Registry.LookupType(name: "U64");
            TypeInfo? s32Type = ctx.Registry.LookupType(name: "S32");
            if (paramType is ChoiceTypeInfo && record.Name == "S64" && s64Type != null)
            {
                ctx.VariantBodies[key: routine.RegistryKey] = BuildLlvmIntrinsicCallBody(
                    intrinsicName: "sign_extend",
                    fromType: paramType,
                    toType: s64Type,
                    paramName: paramName);
                return true;
            }

            // S32.create(from: Choice) — a choice is S32-backed, so its discriminant reinterprets to S32
            // with no width change. Used by the choice `eq` derive + choice pattern lowering to compare
            // discriminants via S32.eq (icmp eq i32) instead of the codegen-side `is` primitive.
            if (paramType is ChoiceTypeInfo && record.Name == "S32" && s32Type != null)
            {
                ctx.VariantBodies[key: routine.RegistryKey] = BuildLlvmIntrinsicCallBody(
                    intrinsicName: "reinterpret_bits",
                    fromType: paramType,
                    toType: s32Type,
                    paramName: paramName);
                return true;
            }

            if (paramType is FlagsTypeInfo && record.Name == "U64" && u64Type != null)
            {
                ctx.VariantBodies[key: routine.RegistryKey] = BuildLlvmIntrinsicCallBody(
                    intrinsicName: "reinterpret_bits",
                    fromType: paramType,
                    toType: u64Type,
                    paramName: paramName);
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Synthesizes the field-independent <c>assign</c>/<c>duplicate</c> copy bodies and the composite
    /// <c>serialize</c> body BEFORE the opaque-backend skip, so @llvm primitives get real (trivial,
    /// LLVM-inlined) bodies. Returns <c>true</c> if a body was synthesized (or the case was consumed).
    /// </summary>
    private bool TryHandleRecordCopyOrSerialize(RoutineInfo routine, RecordTypeInfo record,
        TypeInfo textType)
    {
        switch (routine.Name)
        {
            case "assign":
                ctx.VariantBodies[key: routine.RegistryKey] =
                    (record.IsGenericDefinition
                        ? null
                        : CloneUniversalDeriveBody(ownerType: record, synthesized: routine,
                            memberRoutineName: "assign"))
                    ?? BuildRecordCopyBody(record: record);
                return true;
            case "duplicate":
                // Deep `copy` forwards to `store` — cloned from the `@overridable routine T.copy()`
                // derive template (`return me.assign()`); falls back to the C# builder.
                ctx.VariantBodies[key: routine.RegistryKey] =
                    CloneUniversalDeriveBody(ownerType: record, synthesized: routine,
                        memberRoutineName: "duplicate")
                    ?? BuildCloneViaCopyBody(ownerType: record);
                return true;
            case SerializeMemberRoutineName when !record.IsGenericDefinition:
                // A COMPOSITE record clones the universal `@overridable routine T.serialize()` derive
                // template (comptime `expand` field-walk into a `Dict[Text, SerialValue]` arm). A type
                // that boxes into its OWN SerialValue arm keeps the C# builder: the scalar leaves
                // (S8..U64/F32/F64/Bool/Moment/Bytes/Text — a matching scalar arm) and zero-field opaque
                // @llvm records self-box, not field-walk (an empty `expand` would wrongly yield `{}`).
                // "Composite" = has RF fields, no direct @llvm backend, AND no scalar arm of its own.
                bool serializeComposite = record.BackendType == null &&
                    record.MemberVariables.Count > 0 &&
                    ctx.Registry.LookupType(name: "SerialValue") is VariantTypeInfo serialValueDef &&
                    FindScalarArm(serialValue: serialValueDef, fieldType: record) == null;
                Statement? recSer =
                    (serializeComposite
                        ? CloneUniversalDeriveBody(ownerType: record, synthesized: routine,
                            memberRoutineName: SerializeMemberRoutineName)
                        : null)
                    ?? BuildSerializeBody(owner: record, fields: record.MemberVariables,
                        textType: textType);
                if (recSer != null) ctx.VariantBodies[key: routine.RegistryKey] = recSer;
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Synthesizes the field-dependent wired bodies for a concrete (non-opaque-backend) record:
    /// eq/cmp/represent/serialize/diagnose/hash. Cloned from the universal derive templates with a
    /// C# builder fallback.
    /// </summary>
    private void HandleRecordFieldWiredRoutine(RoutineInfo routine, RecordTypeInfo record,
        TypeInfo textType, TypeInfo boolType, TypeInfo? s32Type)
    {
        switch (routine.Name)
        {
            case "eq":
            {
                // eq generation requires knowing the concrete field types at body-gen time.
                // Generic definitions are handled per concrete instantiation via GMP.
                if (record.IsGenericDefinition) break;
                // The record's eq body is CLONED from the universal `@overridable routine T.eq()`
                // RazorForge derive template (comptime `expand` field-walk), moving the logic out of
                // C#. Falls back to the C# builder if the template isn't present.
                ctx.VariantBodies[key: routine.RegistryKey] =
                    CloneUniversalDeriveBody(ownerType: record, synthesized: routine,
                        memberRoutineName: "eq")
                    ?? BuildEqBody(ownerType: record, fields: record.MemberVariables,
                        boolType: boolType);
                break;
            }

            case "cmp":
            {
                if (s32Type == null) break;
                ctx.VariantBodies[key: routine.RegistryKey] =
                    CloneUniversalDeriveBody(ownerType: record, synthesized: routine,
                        memberRoutineName: "cmp")
                    ?? BuildCmpBody(ownerType: record, fields: record.MemberVariables,
                        s32Type: s32Type, boolType: boolType);
                break;
            }

            // (store / clone handled above, before the opaque-backend skip.)

            case RepresentMemberRoutineName:
                // A record's represent body is CLONED from the universal `@overridable routine
                // T.represent()` RazorForge derive template (comptime `expand` field-walk), moving the
                // logic out of C#. Falls back to the C# builder if the template isn't present.
                ctx.VariantBodies[key: routine.RegistryKey] =
                    CloneUniversalDeriveBody(ownerType: record, synthesized: routine,
                        memberRoutineName: RepresentMemberRoutineName)
                    ?? BuildTextBody(ownerType: record, fields: record.MemberVariables,
                        textType: textType, diagnose: false);
                break;

            case SerializeMemberRoutineName:
            {
                // Handled before the opaque-backend skip (see the switch above) so @llvm scalar leaves
                // get a boxing body; composite records are covered there as well. Nothing to do here.
                break;
            }

            case DiagnoseMemberRoutineName:
                ctx.VariantBodies[key: routine.RegistryKey] =
                    CloneUniversalDeriveBody(ownerType: record, synthesized: routine,
                        memberRoutineName: DiagnoseMemberRoutineName)
                    ?? BuildTextBody(ownerType: record, fields: record.MemberVariables,
                        textType: textType, diagnose: true);
                break;

            case HashMemberRoutineName when routine.Parameters.Count == 0:
            {
                // Generic definitions allowed: monomorphization substitutes type params.
                // Concrete records clone the universal `@overridable routine T.hash()` derive
                // template (comptime `expand` XOR-fold); generic defs stay on the C# builder.
                TypeInfo? u64Type = ctx.Registry.LookupType(name: "U64");
                if (u64Type == null) break;
                ctx.VariantBodies[key: routine.RegistryKey] =
                    (record.IsGenericDefinition
                        ? null
                        : CloneUniversalDeriveBody(ownerType: record, synthesized: routine,
                            memberRoutineName: HashMemberRoutineName))
                    ?? BuildHashBody(ownerType: record, fields: record.MemberVariables,
                        u64Type: u64Type);
                break;
            }

            case HashMemberRoutineName when routine.Parameters.Count == 2:
            {
                if (record.IsGenericDefinition) break;
                TypeInfo? u64Type = ctx.Registry.LookupType(name: "U64");
                if (u64Type == null) break;
                ctx.VariantBodies[key: routine.RegistryKey] =
                    CloneUniversalDeriveBody(ownerType: record, synthesized: routine,
                        memberRoutineName: HashMemberRoutineName)
                    ?? BuildSecureHashBody(ownerType: record,
                        fields: record.MemberVariables,
                        u64Type: u64Type);
                break;
            }
        }
    }

    private void HandleEntity(RoutineInfo routine, EntityTypeInfo entity, TypeInfo textType,
        TypeInfo boolType)
    {
        // Generic definitions allowed: monomorphization substitutes type params via _typeSubstitutions.
        switch (routine.Name)
        {
            case RepresentMemberRoutineName:
                // Cloned from the universal `@overridable routine T.represent()` derive template
                // (comptime `expand` field-walk), same as records/tuples; a routine-typed member now
                // renders its first-class `represent` (the signature) instead of the old `<routine>`.
                ctx.VariantBodies[key: routine.RegistryKey] =
                    CloneUniversalDeriveBody(ownerType: entity, synthesized: routine,
                        memberRoutineName: RepresentMemberRoutineName)
                    ?? BuildTextBody(ownerType: entity, fields: entity.MemberVariables,
                        textType: textType, diagnose: false);
                break;

            case DiagnoseMemberRoutineName:
                ctx.VariantBodies[key: routine.RegistryKey] =
                    CloneUniversalDeriveBody(ownerType: entity, synthesized: routine,
                        memberRoutineName: DiagnoseMemberRoutineName)
                    ?? BuildTextBody(ownerType: entity, fields: entity.MemberVariables,
                        textType: textType, diagnose: true);
                break;

            case SerializeMemberRoutineName:
            {
                if (entity.IsGenericDefinition) break;
                // A composite entity clones the universal `@overridable routine T.serialize()` derive
                // template — same as records, but the template's per-field `m.obeying(...)` gate now
                // earns its keep: an entity MAY hold a routine-typed field (records can't, RF-S412),
                // which the gate lowers to the `<routine>` placeholder. Zero-field entities keep the
                // C# builder (opaque Text box). Falls back to C# too.
                Statement? serBody =
                    (entity.MemberVariables.Count > 0
                        ? CloneUniversalDeriveBody(ownerType: entity, synthesized: routine,
                            memberRoutineName: SerializeMemberRoutineName)
                        : null)
                    ?? BuildSerializeBody(owner: entity, fields: entity.MemberVariables,
                        textType: textType);
                if (serBody != null) ctx.VariantBodies[key: routine.RegistryKey] = serBody;
                break;
            }

            case "eq":
                ctx.VariantBodies[key: routine.RegistryKey] =
                    CloneUniversalDeriveBody(ownerType: entity, synthesized: routine,
                        memberRoutineName: "eq")
                    ?? (entity.MemberVariables.Count == 0
                        ? BuildReturnTrueBody(boolType: boolType)
                        : BuildEqBody(ownerType: entity, fields: entity.MemberVariables,
                            boolType: boolType));
                break;

            case HashMemberRoutineName
                when entity.MemberVariables.Count > 0 && routine.Parameters.Count == 0:
            {
                TypeInfo? u64Type = ctx.Registry.LookupType(name: "U64");
                if (u64Type == null) break;
                ctx.VariantBodies[key: routine.RegistryKey] =
                    (entity.IsGenericDefinition
                        ? null
                        : CloneUniversalDeriveBody(ownerType: entity, synthesized: routine,
                            memberRoutineName: HashMemberRoutineName))
                    ?? BuildHashBody(ownerType: entity, fields: entity.MemberVariables,
                        u64Type: u64Type);
                break;
            }

            case HashMemberRoutineName
                when entity.MemberVariables.Count > 0 && routine.Parameters.Count == 2:
            {
                TypeInfo? u64Type = ctx.Registry.LookupType(name: "U64");
                if (u64Type == null) break;
                ctx.VariantBodies[key: routine.RegistryKey] =
                    (entity.IsGenericDefinition
                        ? null
                        : CloneUniversalDeriveBody(ownerType: entity, synthesized: routine,
                            memberRoutineName: HashMemberRoutineName))
                    ?? BuildSecureHashBody(ownerType: entity,
                        fields: entity.MemberVariables,
                        u64Type: u64Type);
                break;
            }

            // Text.create(from: T) -> return from.represent()
            case "create" when entity.Name == "Text" && routine.Parameters.Count == 1:
            {
                TypeInfo paramType = routine.Parameters[index: 0].Type;
                string paramName = routine.Parameters[index: 0].Name;
                var fromRef = new IdentifierExpression(Name: paramName, Location: _synthLoc)
                {
                    ResolvedType = paramType
                };
                var representCall = new CallExpression(
                    Callee: new MemberExpression(Object: fromRef,
                        MemberName: RepresentMemberRoutineName,
                        Location: _synthLoc) { ResolvedType = textType },
                    Arguments: [],
                    Location: _synthLoc) { ResolvedType = textType };
                ctx.VariantBodies[key: routine.RegistryKey] =
                    new ReturnStatement(Value: representCall, Location: _synthLoc);
                break;
            }
        }
    }

    private void HandleCrashable(RoutineInfo routine, CrashableTypeInfo crashable,
        TypeInfo textType)
    {
        switch (routine.Name)
        {
            case RepresentMemberRoutineName:
                ctx.VariantBodies[key: routine.RegistryKey] =
                    BuildCrashableRepresentBody(crashable: crashable);
                break;

            case DiagnoseMemberRoutineName:
                ctx.VariantBodies[key: routine.RegistryKey] =
                    BuildCrashableDiagnoseBody(crashable: crashable, textType: textType);
                break;

            case "crash_title":
                ctx.VariantBodies[key: routine.RegistryKey] = new ReturnStatement(
                    Value: new LiteralExpression(Value: crashable.CrashTitle,
                        LiteralType: TokenType.TextLiteral,
                        Location: _synthLoc) { ResolvedType = textType },
                    Location: _synthLoc);
                break;

            case Declaration.RuntimeContract.CrashMessage:
                // Default crash_message for a crashable that declared none: fall back to the
                // always-synthesized title. A user-declared crash_message overrides this (the wired
                // routine is only registered when absent — see AutoWiredRegistrationPass).
                ctx.VariantBodies[key: routine.RegistryKey] =
                    BuildCrashableCrashMessageBody(crashable: crashable);
                break;
        }
    }

    private void HandleChoice(RoutineInfo routine, ChoiceTypeInfo choice, TypeInfo textType,
        TypeInfo boolType, TypeInfo? logicBreachedErrorType, TypeInfo? u64Type,
        TypeInfo? s64Type, TypeInfo? listTypeDef)
    {
        switch (routine.Name)
        {
            case "eq":
            {
                // Choice `eq` = discriminant equality. A choice is S32-backed, so reinterpret both sides
                // to its underlying integer (S32) and compare with `==`, which OperatorLoweringPass lowers
                // to `S32.eq` (icmp eq i32). This keeps the `is` operator OUT of codegen — no EmitChoiceIs,
                // no eq->is->eq recursion (the conversion is a reinterpret, the compare is S32's wired eq).
                TypeInfo underlying = choice.UnderlyingType
                    ?? ctx.Registry.LookupType(name: "S32")!;
                ctx.VariantBodies[key: routine.RegistryKey] = BuildNumericEqBodyViaConversion(
                    ownerType: choice,
                    conversionTypeName: underlying.Name,
                    conversionType: underlying,
                    boolType: boolType);
                break;
            }

            case HashMemberRoutineName
                when s64Type != null && u64Type != null && routine.Parameters.Count == 0:
                ctx.VariantBodies[key: routine.RegistryKey] = BuildNumericHashBodyViaConversion(
                    ownerType: choice,
                    conversionTypeName: "S64",
                    conversionType: s64Type,
                    u64Type: u64Type);
                break;

            case HashMemberRoutineName
                when s64Type != null && u64Type != null && routine.Parameters.Count == 2:
                ctx.VariantBodies[key: routine.RegistryKey] =
                    BuildNumericSecureHashBodyViaConversion(ownerType: choice,
                        conversionTypeName: "S64",
                        conversionType: s64Type,
                        u64Type: u64Type);
                break;

            case "all_cases":
                // Cloned from the `@overridable … needs T is ChoiceType` DeriveText template (caseof unroll
                // reconstructing each case via `Me(from: $valueof(c))`); C# BuildAllCasesBody is the fallback.
                ctx.VariantBodies[key: routine.RegistryKey] =
                    CloneUniversalDeriveBody(ownerType: choice, synthesized: routine,
                        memberRoutineName: "all_cases")
                    ?? (listTypeDef != null
                        ? BuildAllCasesBody(
                            memberNames: choice.Cases.Select(c => c.Name).ToList(),
                            elementType: choice,
                            listType: ctx.Registry.GetOrCreateResolution(genericDef: listTypeDef,
                                typeArguments: [choice]))
                        : null!);
                break;

            case "count" when u64Type != null:
                // Buildtime constant: the declared case count is known here, so emit `return <N>_u64`
                // directly instead of a `caseof` unroll that increments N times at runtime.
                ctx.VariantBodies[key: routine.RegistryKey] =
                    BuildCountBody(count: choice.Cases.Count, u64Type: u64Type);
                break;

            case CreateMemberRoutineName
                when !routine.IsFailable && routine.Parameters is [{ Name: "from" }]:
                // Reverse constructor `Choice.create(from: S32)` — reinterpret the discriminant bits.
                ctx.VariantBodies[key: routine.RegistryKey] = BuildLlvmIntrinsicCallBody(
                    intrinsicName: "reinterpret_bits",
                    fromType: routine.Parameters[0].Type!,
                    toType: choice,
                    paramName: "from");
                break;

            case RepresentMemberRoutineName:
                // Cloned from the `@override … needs T is ChoiceType` derive template (VALUE-dispatch
                // via `caseof`); falls back to the C# builder.
                ctx.VariantBodies[key: routine.RegistryKey] =
                    CloneUniversalDeriveBody(ownerType: choice, synthesized: routine,
                        memberRoutineName: RepresentMemberRoutineName)
                    ?? BuildChoiceRepresentBody(choice: choice, textType: textType,
                        logicBreachedErrorType: logicBreachedErrorType);
                break;

            case DiagnoseMemberRoutineName:
                ctx.VariantBodies[key: routine.RegistryKey] =
                    CloneUniversalDeriveBody(ownerType: choice, synthesized: routine,
                        memberRoutineName: DiagnoseMemberRoutineName)
                    ?? BuildChoiceDiagnoseBody(choice: choice, textType: textType,
                        logicBreachedErrorType: logicBreachedErrorType);
                break;

            case SerializeMemberRoutineName when !choice.IsGenericDefinition:
                // A `choice` has no serializable payload, so it boxes its `represent()` Text (the zero-field
                // path of BuildSerializeBody) — the fallback the derived composite's `obeying` else-branch
                // used to produce, now that serialize is universal.
                ctx.VariantBodies[key: routine.RegistryKey] =
                    BuildSerializeBody(owner: choice, fields: [], textType: textType);
                break;

            case CreateMemberRoutineName when routine.IsFailable:
                // The failable Text -> ChoiceType conversion (bare name `create` + IsFailable).
                // Text -> ChoiceType conversion is not implementable at the RF level;
                // this always crashes. The body is unreachable in well-typed programs.
                ctx.VariantBodies[key: routine.RegistryKey] =
                    BuildBreachStatement(logicBreachedErrorType: logicBreachedErrorType);
                break;

            case "assign":
                ctx.VariantBodies[key: routine.RegistryKey] = BuildReturnMeBody(ownerType: choice);
                break;

            case "duplicate":
                ctx.VariantBodies[key: routine.RegistryKey] =
                    BuildCloneViaCopyBody(ownerType: choice);
                break;
        }
    }

    /// <summary>
    /// Builds the body: <c>return me == you</c> for choice and flags types.
    /// The <c>BinaryExpression(Equal)</c> lowers to <c>icmp eq i32</c> (choice) or
    /// <c>icmp eq i64</c> (flags) in <c>EmitPrimitiveBinaryOp</c>.
    /// </summary>
    private static ReturnStatement BuildEqBodyNumeric(TypeInfo ownerType, TypeInfo boolType,
        bool isChoice)
    {
        var meRef = new IdentifierExpression(Name: "me", Location: _synthLoc)
        {
            ResolvedType = ownerType
        };
        var youRef = new IdentifierExpression(Name: "you", Location: _synthLoc)
        {
            ResolvedType = ownerType
        };
        // Choice: BinaryOperator.Is -> EmitChoiceIs -> icmp eq i32 (no eq recursion).
        // Flags: BinaryOperator.Equal stays — OperatorLoweringPass skips it for flags,
        // and codegen emits icmp eq i64 via the flags-specific handler.
        BinaryOperator op = isChoice
            ? BinaryOperator.Is
            : BinaryOperator.Equal;
        var cmp = new BinaryExpression(Left: meRef,
            Operator: op,
            Right: youRef,
            Location: _synthLoc) { ResolvedType = boolType };
        return new ReturnStatement(Value: cmp, Location: _synthLoc);
    }

    /// <summary>
    /// Builds the body: <c>return me.f1 == you.f1 and me.f2 == you.f2 and ...</c>
    /// Zero-field types: <c>return true</c>.
    /// </summary>
    private static ReturnStatement BuildEqBody(TypeInfo ownerType, List<MemberVariableInfo> fields,
        TypeInfo boolType)
    {
        if (fields.Count == 0)
        {
            return new ReturnStatement(
                Value: new LiteralExpression(Value: true,
                    LiteralType: TokenType.True,
                    Location: _synthLoc) { ResolvedType = boolType },
                Location: _synthLoc);
        }

        Expression? combined = null;
        foreach (MemberVariableInfo field in fields)
        {
            // None fields carry no information — two None values are always equal; skip them
            // to avoid emitting calls to None.eq (void params, illegal in LLVM IR).
            if (field.Type.IsNone) continue;

            var lhs = new MemberExpression(
                Object: new IdentifierExpression(Name: "me", Location: _synthLoc)
                {
                    ResolvedType = ownerType
                },
                MemberName: field.Name,
                Location: _synthLoc) { ResolvedType = field.Type };

            var rhs = new MemberExpression(
                Object: new IdentifierExpression(Name: "you", Location: _synthLoc)
                {
                    ResolvedType = ownerType
                },
                MemberName: field.Name,
                Location: _synthLoc) { ResolvedType = field.Type };

            var cmp = new BinaryExpression(Left: lhs,
                Operator: BinaryOperator.Equal,
                Right: rhs,
                Location: _synthLoc) { ResolvedType = boolType };

            combined = combined == null
                ? cmp
                : new BinaryExpression(Left: combined,
                    Operator: BinaryOperator.And,
                    Right: cmp,
                    Location: _synthLoc) { ResolvedType = boolType };
        }

        return new ReturnStatement(
            Value: combined ??
                   new LiteralExpression(Value: true,
                       LiteralType: TokenType.True,
                       Location: _synthLoc) { ResolvedType = boolType },
            Location: _synthLoc);
    }

    /// <summary>
    /// Builds the body: <c>return true</c> for zero-field entity types.
    /// Zero-field entities have no distinguishing state, so any two instances are structurally equal.
    /// </summary>
    /// <summary>
    /// Builds the body: <c>return me</c>. Used for synthesized <c>store()</c> on
    /// Assignable types. Codegen emits a bitwise copy of the receiver into the
    /// return slot — no memberRoutine dispatch overhead at the call site.
    /// </summary>
    private static ReturnStatement BuildReturnMeBody(TypeInfo ownerType)
    {
        var meRef = new IdentifierExpression(Name: "me", Location: _synthLoc)
        {
            ResolvedType = ownerType
        };
        return new ReturnStatement(Value: meRef, Location: _synthLoc);
    }

    /// <summary>
    /// Builds the synthesized record <c>store</c> body. Symmetric with
    /// <see cref="BuildDestroyBody"/>: if any field needs a retaining copy (e.g. a
    /// refcounted <c>Decimal</c>/<c>Text</c> field whose own <c>store</c> bumps a shared
    /// controller), reconstruct the record memberwise — <c>return Owner(f: me.f.assign(),
    /// g: me.g, …)</c> — so those field refcounts are bumped to balance the per-field
    /// <c>destroy</c> at teardown. Without this, the value-copy shares field handles at
    /// refcount 1 and both copies free them → double-free.
    ///
    /// Pure value records (all fields trivially copyable) and @llvm-backed primitives keep
    /// the cheap shallow <c>return me</c>.
    /// </summary>
    private ReturnStatement BuildRecordCopyBody(RecordTypeInfo record)
    {
        // @llvm-backed primitives (S64, Bool, …) have no composite fields to recurse into.
        if (record.BackendType != null || record.MemberVariables is null or { Count: 0 })
            return BuildReturnMeBody(ownerType: record);

        // Only reconstruct when at least one field genuinely needs a retaining copy.
        // Otherwise the shallow byte-copy is both correct and cheaper.
        var anyRetaining = record.MemberVariables.Any(predicate: f => ctx.Registry
           .GetLifecycle(type: f.Type)
           .Store is not null);
        if (!anyRetaining)
            return BuildReturnMeBody(ownerType: record);

        var memberArgs = new List<(string Name, Expression Value)>(
            capacity: record.MemberVariables.Count);
        foreach (MemberVariableInfo field in record.MemberVariables)
        {
            var meRef = new IdentifierExpression(Name: "me", Location: _synthLoc)
            {
                ResolvedType = record
            };
            var fieldRef =
                new MemberExpression(Object: meRef, MemberName: field.Name, Location: _synthLoc)
                {
                    ResolvedType = field.Type
                };

            // Retaining field → me.f.assign() (bumps its refcount); value field → me.f (shallow).
            Expression argExpr = ctx.Registry.GetLifecycle(type: field.Type)
                                    .Store is not null
                ? new CallExpression(
                    Callee: new MemberExpression(Object: fieldRef,
                        MemberName: "assign",
                        Location: _synthLoc) { ResolvedType = field.Type },
                    Arguments: [],
                    Location: _synthLoc) { ResolvedType = field.Type }
                : fieldRef;
            memberArgs.Add(item: (field.Name, argExpr));
        }

        var creator =
            new CreatorExpression(TypeName: record.Name,
                TypeArguments: null,
                MemberVariables: memberArgs,
                Location: _synthLoc)
            {
                ResolvedType = record, LoweringKind = CallLoweringKind.TypeConstructor
            };
        return new ReturnStatement(Value: creator, Location: _synthLoc);
    }

    /// <summary>
    /// Builds the body: <c>return me.assign()</c>. Used for synthesized <c>clone()</c>
    /// on Assignable types — clone is an Assignable-implied alias for the explicit
    /// copy verb, so it just forwards.
    /// </summary>
    private static ReturnStatement BuildCloneViaCopyBody(TypeInfo ownerType)
    {
        var meRef = new IdentifierExpression(Name: "me", Location: _synthLoc)
        {
            ResolvedType = ownerType
        };
        var copyMember =
            new MemberExpression(Object: meRef, MemberName: "assign", Location: _synthLoc)
            {
                ResolvedType = ownerType
            };
        var copyCall =
            new CallExpression(Callee: copyMember, Arguments: [], Location: _synthLoc)
            {
                ResolvedType = ownerType
            };
        return new ReturnStatement(Value: copyCall, Location: _synthLoc);
    }

    private static ReturnStatement BuildReturnTrueBody(TypeInfo boolType)
    {
        return new ReturnStatement(
            Value: new LiteralExpression(Value: true,
                LiteralType: TokenType.True,
                Location: _synthLoc) { ResolvedType = boolType },
            Location: _synthLoc);
    }

    //  hash

    /// <summary>
    /// Builds a choice/flags <c>count()</c> body as a BUILDTIME CONSTANT: <c>return &lt;N&gt;_u64</c>,
    /// where N is the declared case/member count (known at build time). Constant-time at runtime — no
    /// <c>caseof</c> unroll + per-case increment. Replaces the DeriveText template for <c>count</c>.
    /// </summary>
    private static ReturnStatement BuildCountBody(int count, TypeInfo u64Type)
        => new(Value: new LiteralExpression(Value: (ulong)count,
                LiteralType: TokenType.U64Literal, Location: _synthLoc) { ResolvedType = u64Type },
            Location: _synthLoc);

    /// <summary>
    /// Builds the body: <c>return me.f1.hash() ^ me.f2.hash() ^ ...</c>.
    /// Zero-field types: <c>return 0_u64</c>.
    /// </summary>
    private ReturnStatement BuildHashBody(TypeInfo ownerType, List<MemberVariableInfo> fields,
        TypeInfo u64Type)
    {
        if (fields.Count == 0)
        {
            return new ReturnStatement(
                Value: new LiteralExpression(Value: 0UL,
                    LiteralType: TokenType.U64Literal,
                    Location: _synthLoc) { ResolvedType = u64Type },
                Location: _synthLoc);
        }

        // Pre-resolve U64.bitxor so synthesized CallExpression nodes carry a concrete
        // ResolvedRoutine. Without it, codegen's DirectMemberRoutine path throws when it
        // can't determine the receiver type for the XOR accumulator call.
        RoutineInfo? u64Bitxor =
            ctx.Registry.LookupMemberRoutine(type: u64Type, memberRoutineName: BitXorMemberRoutineName);

        Expression? accum = null;
        foreach (MemberVariableInfo field in fields)
        {
            var meRef = new IdentifierExpression(Name: "me", Location: _synthLoc)
            {
                ResolvedType = ownerType
            };
            var fieldAccess =
                new MemberExpression(Object: meRef, MemberName: field.Name, Location: _synthLoc)
                {
                    ResolvedType = field.Type
                };
            var hashMemberRoutine = new MemberExpression(
                Object: fieldAccess,
                MemberName: HashMemberRoutineName,
                Location: _synthLoc) { ResolvedType = u64Type };
            RoutineInfo? fieldHashRoutine = ctx.Registry.LookupMemberRoutineOverload(type: field.Type,
                memberRoutineName: HashMemberRoutineName,
                argTypes: []);
            Expression fieldHash =
                new CallExpression(Callee: hashMemberRoutine, Arguments: [], Location: _synthLoc)
                {
                    ResolvedType = u64Type,
                    ResolvedRoutine = fieldHashRoutine,
                    LoweringKind = CallLoweringKind.DirectMemberRoutine
                };

            if (accum == null)
            {
                accum = fieldHash;
            }
            else
            {
                // XOR the accumulated hash with this field's hash via bitxor memberRoutine call.
                // BinaryExpression(BitwiseXor) must be lowered before codegen; synthesized
                // bodies bypass the lowering pass, so we emit the memberRoutine call directly.
                accum = new CallExpression(
                    Callee: new MemberExpression(Object: accum,
                        MemberName: BitXorMemberRoutineName,
                        Location: _synthLoc) { ResolvedType = u64Type },
                    Arguments:
                    [
                        new NamedArgumentExpression(Name: "you",
                            Value: fieldHash,
                            Location: _synthLoc)
                    ],
                    Location: _synthLoc)
                {
                    ResolvedType = u64Type,
                    ResolvedRoutine = u64Bitxor,
                    LoweringKind = CallLoweringKind.DirectMemberRoutine
                };
            }
        }

        return new ReturnStatement(Value: accum!, Location: _synthLoc);
    }

    /// <summary>
    /// Builds the body: <c>return ConversionType(from: me).hash()</c>.
    /// Used for Choice (<c>S64(from: me).hash()</c>) and Flags (<c>U64(from: me).hash()</c>).
    /// The numeric create lowers via the existing codegen numeric-create path; <c>hash</c>
    /// on the result delegates to the primitive type's xxHash64 implementation.
    /// </summary>
    /// <summary>
    /// Builds the body: <c>return ConversionType(from: me) == ConversionType(from: you)</c>.
    /// Used for Choice <c>eq</c>: reinterpret both discriminants to the underlying integer (S32) and
    /// compare via <c>==</c>, which OperatorLoweringPass lowers to the integer type's wired <c>eq</c>
    /// (<c>icmp eq i32</c>). The conversion is a reinterpret and the compare is the primitive's own eq,
    /// so this neither uses the <c>is</c> operator nor recurses into the choice's own <c>eq</c>.
    /// </summary>
    private static ReturnStatement BuildNumericEqBodyViaConversion(TypeInfo ownerType,
        string conversionTypeName, TypeInfo conversionType, TypeInfo boolType)
    {
        CreatorExpression Reinterpret(string localName) =>
            new(TypeName: conversionTypeName,
                TypeArguments: null,
                MemberVariables:
                [
                    ("from", new IdentifierExpression(Name: localName, Location: _synthLoc)
                        { ResolvedType = ownerType })
                ],
                Location: _synthLoc)
            {
                ResolvedType = conversionType, LoweringKind = CallLoweringKind.TypeConstructor
            };

        var cmp = new BinaryExpression(
            Left: Reinterpret(localName: "me"),
            Operator: BinaryOperator.Equal,
            Right: Reinterpret(localName: "you"),
            Location: _synthLoc) { ResolvedType = boolType };
        return new ReturnStatement(Value: cmp, Location: _synthLoc);
    }

    private static ReturnStatement BuildNumericHashBodyViaConversion(TypeInfo ownerType,
        string conversionTypeName, TypeInfo conversionType, TypeInfo u64Type)
    {
        var meRef = new IdentifierExpression(Name: "me", Location: _synthLoc)
        {
            ResolvedType = ownerType
        };
        var creator =
            new CreatorExpression(TypeName: conversionTypeName,
                TypeArguments: null,
                MemberVariables: [("from", meRef)],
                Location: _synthLoc)
            {
                ResolvedType = conversionType, LoweringKind = CallLoweringKind.TypeConstructor
            };
        var hashCall = new CallExpression(
            Callee: new MemberExpression(Object: creator,
                MemberName: HashMemberRoutineName,
                Location: _synthLoc) { ResolvedType = u64Type },
            Arguments: [],
            Location: _synthLoc) { ResolvedType = u64Type };
        return new ReturnStatement(Value: hashCall, Location: _synthLoc);
    }

    /// <summary>
    /// Builds the body: <c>return [Member1, Member2, ...]</c> as a list literal.
    /// Used for <c>all_cases()</c> on choice and flags types.
    /// </summary>
    private static ReturnStatement BuildAllCasesBody(List<string> memberNames,
        TypeInfo elementType, TypeInfo listType)
    {
        var elements = memberNames.Select(name =>
                                       (Expression)new IdentifierExpression(Name: name,
                                           Location: _synthLoc) { ResolvedType = elementType })
                                  .ToList();
        return new ReturnStatement(
            Value: new ListLiteralExpression(Elements: elements,
                ElementType: null,
                Location: _synthLoc) { ResolvedType = listType },
            Location: _synthLoc);
    }

    //  Numeric create (sign_extend / reinterpret_bits)

    /// <summary>
    /// Builds: <c>return intrinsicName[From, To](value: paramName)</c>.
    /// Used for <c>S64.create(from: Choice)</c> via <c>sign_extend</c> and
    /// <c>U64.create(from: Flags)</c> via <c>reinterpret_bits</c>.
    /// </summary>
    private static ReturnStatement BuildLlvmIntrinsicCallBody(string intrinsicName,
        TypeInfo fromType, TypeInfo toType, string paramName)
    {
        var fromRef = new IdentifierExpression(Name: paramName, Location: _synthLoc)
        {
            ResolvedType = fromType
        };
        var typeArgFrom = new TypeExpression(
            Name: fromType.Name,
            GenericArguments: null,
            Location: _synthLoc) { ResolvedType = fromType };
        var typeArgTo =
            new TypeExpression(Name: toType.Name, GenericArguments: null, Location: _synthLoc)
            {
                ResolvedType = toType
            };
        var call = new CallExpression(
            Callee: new IdentifierExpression(Name: intrinsicName, Location: _synthLoc)
            {
                ResolvedType = toType
            },
            Arguments:
            [new NamedArgumentExpression(Name: "value", Value: fromRef, Location: _synthLoc)],
            Location: _synthLoc)
        {
            ResolvedType = toType,
            LoweringKind = CallLoweringKind.LlvmIntrinsic,
            TypeArguments = [typeArgFrom, typeArgTo]
        };
        return new ReturnStatement(Value: call, Location: _synthLoc);
    }

    /// <summary>
    /// Builds: <c>return intrinsicName[reprType](a: me, b: you)</c> for a two-operand LLVM intrinsic
    /// whose params are <c>a</c>/<c>b</c> and whose single type param is the operands' underlying
    /// integer repr. Used for Flags <c>bitor</c>/<c>bitand</c>/<c>bitxor</c> (<c>bit_or</c>/<c>bit_and</c>/
    /// <c>bit_xor</c> → the Flags i64 repr) and <c>eq</c>/<c>ne</c> (<c>int_eq</c>/<c>int_ne</c>). The
    /// intrinsic is emitted directly (not via a surface operator), so OperatorLoweringPass never
    /// recurses back into these bodies.
    /// </summary>
    private static ReturnStatement BuildLlvmBinaryIntrinsicCallBody(string intrinsicName,
        TypeInfo ownerType, TypeInfo reprType, TypeInfo resultType)
    {
        var meRef = new IdentifierExpression(Name: "me", Location: _synthLoc)
        {
            ResolvedType = ownerType
        };
        var youRef = new IdentifierExpression(Name: "you", Location: _synthLoc)
        {
            ResolvedType = ownerType
        };
        var typeArgRepr = new TypeExpression(Name: reprType.Name, GenericArguments: null,
            Location: _synthLoc) { ResolvedType = reprType };
        var call = new CallExpression(
            Callee: new IdentifierExpression(Name: intrinsicName, Location: _synthLoc)
            {
                ResolvedType = resultType
            },
            Arguments:
            [
                new NamedArgumentExpression(Name: "a", Value: meRef, Location: _synthLoc),
                new NamedArgumentExpression(Name: "b", Value: youRef, Location: _synthLoc)
            ],
            Location: _synthLoc)
        {
            ResolvedType = resultType,
            LoweringKind = CallLoweringKind.LlvmIntrinsic,
            TypeArguments = [typeArgRepr]
        };
        return new ReturnStatement(Value: call, Location: _synthLoc);
    }

    //  keyed hash(k0, k1)

    /// <summary>
    /// Builds the body: <c>return me.f1.hash(k0: k0, k1: k1) ^ me.f2.hash(...) ^ ...</c>.
    /// Zero-field types: <c>return 0_u64</c>.
    /// </summary>
    private ReturnStatement BuildSecureHashBody(TypeInfo ownerType,
        List<MemberVariableInfo> fields, TypeInfo u64Type)
    {
        if (fields.Count == 0)
            return new ReturnStatement(
                Value: new LiteralExpression(Value: 0UL,
                    LiteralType: TokenType.U64Literal,
                    Location: _synthLoc) { ResolvedType = u64Type },
                Location: _synthLoc);

        RoutineInfo? u64Bitxor =
            ctx.Registry.LookupMemberRoutine(type: u64Type, memberRoutineName: BitXorMemberRoutineName);

        var k0Ref =
            new IdentifierExpression(Name: "k0", Location: _synthLoc) { ResolvedType = u64Type };
        var k1Ref =
            new IdentifierExpression(Name: "k1", Location: _synthLoc) { ResolvedType = u64Type };

        Expression? accum = null;
        foreach (MemberVariableInfo field in fields)
        {
            var meRef = new IdentifierExpression(Name: "me", Location: _synthLoc)
            {
                ResolvedType = ownerType
            };
            var fieldAccess =
                new MemberExpression(Object: meRef, MemberName: field.Name, Location: _synthLoc)
                {
                    ResolvedType = field.Type
                };
            RoutineInfo? fieldSecureHashRoutine = ctx.Registry.LookupMemberRoutineOverload(
                type: field.Type,
                memberRoutineName: HashMemberRoutineName,
                argTypes: [u64Type, u64Type]);
            Expression fieldHash = new CallExpression(
                Callee: new MemberExpression(Object: fieldAccess,
                    MemberName: HashMemberRoutineName,
                    Location: _synthLoc) { ResolvedType = u64Type },
                Arguments:
                [
                    new NamedArgumentExpression(Name: "k0", Value: k0Ref, Location: _synthLoc),
                    new NamedArgumentExpression(Name: "k1", Value: k1Ref, Location: _synthLoc)
                ],
                Location: _synthLoc)
            {
                ResolvedType = u64Type,
                ResolvedRoutine = fieldSecureHashRoutine,
                LoweringKind = CallLoweringKind.DirectMemberRoutine
            };

            if (accum == null)
            {
                accum = fieldHash;
            }
            else
            {
                accum = new CallExpression(
                    Callee: new MemberExpression(Object: accum,
                        MemberName: BitXorMemberRoutineName,
                        Location: _synthLoc) { ResolvedType = u64Type },
                    Arguments:
                    [
                        new NamedArgumentExpression(Name: "you",
                            Value: fieldHash,
                            Location: _synthLoc)
                    ],
                    Location: _synthLoc)
                {
                    ResolvedType = u64Type,
                    ResolvedRoutine = u64Bitxor,
                    LoweringKind = CallLoweringKind.DirectMemberRoutine
                };
            }
        }

        return new ReturnStatement(Value: accum!, Location: _synthLoc);
    }

    /// <summary>
    /// Builds the body: <c>return ConversionType(from: me).hash(k0: k0, k1: k1)</c>.
    /// Used for Choice (<c>S64(from: me)</c>) and Flags (<c>U64(from: me)</c>).
    /// </summary>
    private static ReturnStatement BuildNumericSecureHashBodyViaConversion(TypeInfo ownerType,
        string conversionTypeName, TypeInfo conversionType, TypeInfo u64Type)
    {
        var meRef = new IdentifierExpression(Name: "me", Location: _synthLoc)
        {
            ResolvedType = ownerType
        };
        var k0Ref =
            new IdentifierExpression(Name: "k0", Location: _synthLoc) { ResolvedType = u64Type };
        var k1Ref =
            new IdentifierExpression(Name: "k1", Location: _synthLoc) { ResolvedType = u64Type };
        var creator =
            new CreatorExpression(TypeName: conversionTypeName,
                TypeArguments: null,
                MemberVariables: [("from", meRef)],
                Location: _synthLoc)
            {
                ResolvedType = conversionType, LoweringKind = CallLoweringKind.TypeConstructor
            };
        var hashCall = new CallExpression(
            Callee: new MemberExpression(Object: creator,
                MemberName: HashMemberRoutineName,
                Location: _synthLoc) { ResolvedType = u64Type },
            Arguments:
            [
                new NamedArgumentExpression(Name: "k0", Value: k0Ref, Location: _synthLoc),
                new NamedArgumentExpression(Name: "k1", Value: k1Ref, Location: _synthLoc)
            ],
            Location: _synthLoc) { ResolvedType = u64Type };
        return new ReturnStatement(Value: hashCall, Location: _synthLoc);
    }

    //  cmp

    /// <summary>
    /// Builds the body: lexicographic field comparison returning S32 (-1/0/1).
    /// <c>var r = me.f1.cmp(you: you.f1); if r != 0 { return r } ...</c>
    /// Zero-field types: <c>return 0_s32</c>.
    /// </summary>
    private static Statement BuildCmpBody(TypeInfo ownerType, List<MemberVariableInfo> fields,
        TypeInfo s32Type, TypeInfo boolType)
    {
        var zeroS32 = new LiteralExpression(
            Value: 0L,
            LiteralType: TokenType.S32Literal,
            Location: _synthLoc) { ResolvedType = s32Type };

        if (fields.Count == 0)
            return new ReturnStatement(Value: zeroS32, Location: _synthLoc);

        var stmts = new List<Statement>(capacity: fields.Count * 2 + 1);
        bool first = true;

        foreach (MemberVariableInfo field in fields)
        {
            var meField = new MemberExpression(
                Object: new IdentifierExpression(Name: "me", Location: _synthLoc)
                {
                    ResolvedType = ownerType
                },
                MemberName: field.Name,
                Location: _synthLoc) { ResolvedType = field.Type };

            var youField = new MemberExpression(
                Object: new IdentifierExpression(Name: "you", Location: _synthLoc)
                {
                    ResolvedType = ownerType
                },
                MemberName: field.Name,
                Location: _synthLoc) { ResolvedType = field.Type };

            var cmpCall = new CallExpression(
                Callee: new MemberExpression(Object: meField,
                    MemberName: "cmp",
                    Location: _synthLoc) { ResolvedType = s32Type },
                Arguments:
                [
                    new NamedArgumentExpression(Name: "you", Value: youField, Location: _synthLoc)
                ],
                Location: _synthLoc) { ResolvedType = s32Type };

            if (first)
            {
                stmts.Add(new DeclarationStatement(Declaration: new VariableDeclaration(Name: "r",
                        Type: null,
                        Initializer: cmpCall,
                        Visibility: VisibilityModifier.Open,
                        Location: _synthLoc),
                    Location: _synthLoc));
                first = false;
            }
            else
            {
                stmts.Add(new AssignmentStatement(
                    Target: new IdentifierExpression(Name: "r", Location: _synthLoc)
                    {
                        ResolvedType = s32Type
                    },
                    Value: cmpCall,
                    Location: _synthLoc));
            }

            var isNonZero = new BinaryExpression(
                Left: new IdentifierExpression(Name: "r", Location: _synthLoc)
                {
                    ResolvedType = s32Type
                },
                Operator: BinaryOperator.NotEqual,
                Right: new LiteralExpression(Value: 0L,
                    LiteralType: TokenType.S32Literal,
                    Location: _synthLoc) { ResolvedType = s32Type },
                Location: _synthLoc) { ResolvedType = boolType };

            stmts.Add(new IfStatement(Condition: isNonZero,
                ThenStatement: new ReturnStatement(
                    Value: new IdentifierExpression(Name: "r", Location: _synthLoc)
                    {
                        ResolvedType = s32Type
                    },
                    Location: _synthLoc),
                ElseStatement: null,
                Location: _synthLoc));
        }

        stmts.Add(new ReturnStatement(Value: zeroS32, Location: _synthLoc));
        return new BlockStatement(Statements: stmts, Location: _synthLoc);
    }

    //  represent / diagnose (record + entity)

    /// <summary>
    /// Builds the body for <c>represent</c> or <c>diagnose</c> on a record or entity.
    /// <list type="bullet">
    ///   <item><c>represent</c>: <c>return f"TypeName(f1: {me.f1}, f2: {me.f2})"</c> -> open+posted fields, named.</item>
    ///   <item><c>diagnose</c>:  <c>return f"Module.TypeName(f1: {me.f1}, [secret] f2: {me.f2})"</c> -> all fields named,
    ///         values via <c>represent</c> (not <c>diagnose</c>) to avoid cascading verbosity.</item>
    /// </list>
    /// Field access via <see cref="MemberExpression"/> works for both records (extractvalue) and
    /// entities (GEP + load).
    /// </summary>
    /// <summary>
    /// Materializes a type's <c>represent</c>/<c>diagnose</c> body by CLONING the universal
    /// <c>@overridable routine T.&lt;memberRoutine&gt;()</c> RazorForge derive template (owner = a
    /// type-parameter placeholder) with the placeholder + <c>Me</c> substituted to the concrete
    /// owner. The clone runs through <see cref="GenericAstRewriter"/>, which unrolls the template's
    /// comptime <c>expand allmemvarof(T)</c> right here (T is now concrete) — so the field-walk logic
    /// lives in RF, not in this C# synthesizer, yet each type still gets its own concrete body (one
    /// per type ⇒ no signature clash). Returns null if the template or its body isn't found.
    /// </summary>
    private Statement? CloneUniversalDeriveBody(TypeInfo ownerType, RoutineInfo synthesized,
        string memberRoutineName)
    {
        // Pick the most-specific kind-matching template for this type (a `needs T is VariantType`
        // override beats the unconstrained base), taking its owner param + body straight from the
        // derive-template store — several same-signature templates coexist there, keyed by gate set.
        (string OwnerParam, Statement Body)? picked =
            ctx.Registry.GetDeriveTemplate(name: memberRoutineName,
                arity: synthesized.Parameters.Count, forType: ownerType);
        if (picked is not { } t) return null;
        Statement body = t.Body;

        // Self-apply a generic-definition owner to its own parameters before substituting the template
        // param into the body. The template binds `T -> ownerType`; if ownerType is the BARE generic def
        // (e.g. `RangeEmittable`, no `[T]`), a body reference that re-wraps `T` — the entity self-free
        // `me.hijack().invalidate()` yields `Hijacked[T]` -> `Hijacked[RangeEmittable]` — is malformed
        // (RangeEmittable is arity-1, used with no argument) and later resolves to junk like
        // `RangeEmittable[RangeEmittable]`, cascading into an unbounded monomorphization regress and an
        // over-prune of the real `Hijacked[RangeEmittable[T]].invalidate`. Binding to the SELF-APPLIED
        // form `RangeEmittable[T]` (T = the def's own parameter) keeps every wrap well-formed; the concrete
        // frame's reachability walk then substitutes that T to the real argument. Concrete owners are not
        // generic definitions, so this is a no-op for them.
        TypeInfo boundOwner = ownerType;
        if (ownerType.IsGenericDefinition && ownerType.GenericParameters is { Count: > 0 } ownParams)
            boundOwner = ctx.Registry.GetOrCreateResolution(genericDef: ownerType,
                typeArguments: ownParams.Select(selector: p => (TypeInfo)new GenericParameterTypeInfo(name: p)).ToList());

        var typeSubs = new Dictionary<string, TypeInfo>
        {
            [t.OwnerParam] = boundOwner,
            ["Me"] = boundOwner
        };
        var stringSubs = typeSubs.ToDictionary(keySelector: kv => kv.Key,
            elementSelector: kv => kv.Value.FullName);
        Statement cloned = GenericAstRewriter.RewriteStatement(stmt: body, subs: stringSubs,
            typeSubs: typeSubs, registry: ctx.Registry, enclosingRoutine: synthesized);

        // Stdlib bodies are stored raw (no SA annotation); backfill `me`'s type so downstream
        // lowering/codegen sees the concrete receiver.
        AstWalker.WalkExpressions(root: cloned, visit: expr =>
        {
            if (expr is IdentifierExpression { Name: "me", ResolvedType: null } id)
                id.ResolvedType = boundOwner;
        });
        // Resolve bare TYPE CONSTRUCTIONS in the cloned derive body. The universal `serialize` template
        // (`var __sv = Dict[Text, SerialValue]()` … `return SerialValue(steal __sv)`) constructs concrete
        // types, but the template is stored RAW (never SA'd) so those constructions carry no ResolvedRoutine/
        // ConstructedType — they'd survive postprocessing (RF-S954 GMCE / RF-S959 unclassified call) and hit
        // codegen. SA normally stamps these (AnalyzeGenericMemberRoutineCall's construction path); do the same
        // HERE, at the per-owner clone, where the concrete constructed type is known — Lumi's "validity
        // precedes generation" applied at the materialization point. No-op for derives with no bare
        // construction (represent/diagnose/cmp), which already work.
        ResolveDeriveConstructions(body: cloned);
        return cloned;
    }

    /// <summary>
    /// Stamps <c>ResolvedRoutine</c>=<c>create</c> + <c>ConstructedType</c> on unresolved bare type
    /// constructions in a freshly-cloned universal-derive body — the raw derive template never went through
    /// SA, so its <c>Dict[Text, SerialValue]()</c> / <c>SerialValue(...)</c> nodes reach here bare. Mirrors
    /// <c>SemanticVerifier</c>'s construction resolution; only touches unresolved nodes (idempotent).
    /// </summary>
    private void ResolveDeriveConstructions(Statement body)
    {
        AstWalker.WalkExpressions(root: body, visit: expr =>
        {
            switch (expr)
            {
                // `Dict[Text, SerialValue]()` — a construction GMCE (Object names the type == MemberRoutineName).
                // RewriteStatement already concretized ConstructedType; resolve its `create` so
                // GenericCallLoweringPass can lower the GMCE.
                case GenericMemberRoutineCallExpression { ResolvedRoutine: null } g
                    when g.Object is IdentifierExpression oid && oid.Name == g.MemberRoutineName:
                {
                    // Build the concrete constructed type: `Dict[Text, SerialValue]()` arrives with
                    // ConstructedType=null (raw template) and TypeArguments=[Text, SerialValue], so resolve
                    // the generic def by name + its resolved type args (a concrete `Dict[Text, SerialValue]`).
                    TypeInfo? ct = g.ConstructedType ?? ResolveConstructedTypeFor(
                        typeName: g.MemberRoutineName, typeArgs: g.TypeArguments);
                    if (ct is { IsGenericDefinition: false }
                        && ctx.Registry.LookupMemberRoutine(type: ct, memberRoutineName: "create") is { } cr)
                    {
                        g.ResolvedRoutine = cr;
                        g.ConstructedType = ct;
                        g.ResolvedType ??= ct;
                    }
                    break;
                }
                // `SerialValue(steal __sv)` — a plain construction CallExpression (callee names a type).
                // SerialValue is a non-generic VARIANT: `SerialValue(dict)` wraps the arg into the matching
                // arm — there is NO `create` routine, so codegen builds from ConstructedType + the arg's type.
                // The RF-S959 gate only needs ConstructedType attached; set it (+ create when one exists,
                // e.g. record/entity types). LoweringKind=TypeConstructor so codegen routes to construction.
                case CallExpression { ResolvedRoutine: null, ConstructedType: null,
                        Callee: IdentifierExpression cid } c
                    when ctx.Registry.LookupType(name: cid.Name) is { IsGenericDefinition: false } ct2:
                {
                    c.ConstructedType = ct2;
                    c.ResolvedType ??= ct2;
                    c.LoweringKind = CallLoweringKind.TypeConstructor;
                    if (ctx.Registry.LookupMemberRoutine(type: ct2, memberRoutineName: "create") is { } cr2)
                        c.ResolvedRoutine = cr2;
                    break;
                }
            }
        });
    }

    /// <summary>
    /// Resolves a bare type-construction's constructed type from its written name + type-argument
    /// expressions (<c>Dict[Text, SerialValue]</c> → the concrete <c>Dict[Text, SerialValue]</c>
    /// resolution). Returns the concrete non-generic type, or null if any part is unresolvable.
    /// </summary>
    private TypeInfo? ResolveConstructedTypeFor(string typeName, List<TypeExpression> typeArgs)
    {
        if (ctx.Registry.LookupType(name: typeName) is not { } baseTy) return null;
        if (typeArgs.Count == 0) return baseTy is { IsGenericDefinition: false } ? baseTy : null;
        var resolved = new List<TypeInfo>(capacity: typeArgs.Count);
        foreach (TypeExpression te in typeArgs)
        {
            if (ResolveTypeExprToInfo(te: te) is not { } a) return null;
            resolved.Add(item: a);
        }
        return ctx.Registry.GetOrCreateResolution(genericDef: baseTy, typeArguments: resolved);
    }

    /// <summary>Resolves a <see cref="TypeExpression"/> (possibly nested-generic) to a concrete
    /// <see cref="TypeInfo"/> via name lookup + resolution; null if any name is unknown.</summary>
    private TypeInfo? ResolveTypeExprToInfo(TypeExpression te)
    {
        if (ctx.Registry.LookupType(name: te.Name) is not { } b) return null;
        if (te.GenericArguments is not { Count: > 0 } ga) return b;
        var args = new List<TypeInfo>(capacity: ga.Count);
        foreach (TypeExpression inner in ga)
        {
            if (ResolveTypeExprToInfo(te: inner) is not { } x) return null;
            args.Add(item: x);
        }
        return ctx.Registry.GetOrCreateResolution(genericDef: b, typeArguments: args);
    }

    private static ReturnStatement BuildTextBody(TypeInfo ownerType,
        List<MemberVariableInfo> fields, TypeInfo textType, bool diagnose)
    {
        var parts = new List<InsertedTextPart>();

        // Emit `me.type_name()` (or `me.full_type_name()` for diagnose) so per-instance
        // monomorphization produces the correct generic-args-included name (e.g.
        // "List[Core.S64]"). Baking ownerType.Name/FullName here freezes the generic-def
        // name ("List") and the type-args are lost in monomorphized bodies.
        var meRef = new IdentifierExpression(Name: "me", Location: _synthLoc)
        {
            ResolvedType = ownerType
        };
        var typeNameCall = new CallExpression(Callee: new MemberExpression(Object: meRef,
                MemberName: diagnose
                    ? "full_type_name"
                    : "type_name",
                Location: _synthLoc) { ResolvedType = textType },
            Arguments: [],
            Location: _synthLoc) { ResolvedType = textType };
        parts.Add(new ExpressionPart(Expression: typeNameCall,
            FormatSpec: null,
            Location: _synthLoc));
        parts.Add(new TextPart(Text: "(", Location: _synthLoc));

        IEnumerable<MemberVariableInfo> visibleMemberVariables = diagnose
            ? fields
            : fields.Where(predicate: f =>
                f.Visibility is VisibilityModifier.Open or VisibilityModifier.Posted);

        bool first = true;
        foreach (MemberVariableInfo field in visibleMemberVariables)
        {
            if (!first)
                parts.Add(new TextPart(Text: ", ", Location: _synthLoc));
            first = false;

            string secretPrefix = diagnose && field.Visibility == VisibilityModifier.Secret
                ? "[secret] "
                : "";
            parts.Add(new TextPart(Text: secretPrefix + field.Name + ": ", Location: _synthLoc));

            // Routine-typed fields (stored lambdas/function pointers in iterator adapters such as
            // WhereIterator's `predicate`) have no represent — emitting one yields an undefined
            // `Routine[...].represent` symbol at link time. Render a stable placeholder instead.
            if (field.Type is RoutineTypeInfo)
            {
                parts.Add(new TextPart(Text: "<routine>", Location: _synthLoc));
                continue;
            }

            var fieldExpr = new MemberExpression(
                Object: new IdentifierExpression(Name: "me", Location: _synthLoc)
                {
                    ResolvedType = ownerType
                },
                MemberName: field.Name,
                Location: _synthLoc) { ResolvedType = field.Type };

            // Always use represent for field values, even inside diagnose.
            // Using diagnose recursively would produce exponentially verbose output.
            parts.Add(new ExpressionPart(Expression: fieldExpr,
                FormatSpec: null,
                Location: _synthLoc));
        }

        parts.Add(new TextPart(Text: ")", Location: _synthLoc));

        var fstring =
            new InsertedTextExpression(Parts: parts, IsRaw: false, Location: _synthLoc)
            {
                ResolvedType = textType
            };

        return new ReturnStatement(Value: fstring, Location: _synthLoc);
    }

    /// <summary>
    /// Builds the auto-derived <c>serialize() -> SerialValue</c> body: a <c>Dict[Text, SerialValue]</c>
    /// of every member variable (name -> value), boxed into the SerialValue <c>Dict</c> arm. Scalar
    /// fields whose type is a direct SerialValue arm are boxed inline; aggregate fields with their own
    /// synthesized <c>serialize()</c> recurse; everything else falls back to <c>Text(field.represent())</c>
    /// (represent is universal, so the fallback always links). Returns null if SerialValue / Dict are
    /// unavailable. Depth-guard for cyclic entity graphs is a follow-up (see serializable-serialvalue-impl).
    /// </summary>
    private ReturnStatement? BuildSerializeBody(TypeInfo owner, List<MemberVariableInfo> fields,
        TypeInfo textType)
    {
        if (ctx.Registry.LookupType(name: "SerialValue") is not VariantTypeInfo serialValue)
            return null;

        // Scalar leaf types (S8..U64, F32/F64, Bool, Moment, Bytes, Text) serialize by boxing THEMSELVES
        // into their own SerialValue arm — not a field walk. This makes `x.serialize()` uniform for every
        // serializable type, so collection `serialize()` can call it per element.
        VariantMemberInfo? ownArm = FindScalarArm(serialValue: serialValue, fieldType: owner);
        if (ownArm?.Type != null)
        {
            var meScalar = new IdentifierExpression(Name: "me", Location: _synthLoc)
            {
                ResolvedType = owner
            };
            var boxedScalar =
                new CreatorExpression(TypeName: serialValue.Name,
                    TypeArguments: null,
                    MemberVariables: [(ownArm.Type.Name, meScalar)],
                    Location: _synthLoc)
                {
                    ResolvedType = serialValue, ConstructedType = serialValue
                };
            return new ReturnStatement(Value: boxedScalar, Location: _synthLoc);
        }

        // OPAQUE-VALUE rule: a type with no direct SerialValue arm AND no RF fields is an opaque scalar
        // (the @llvm wide primitives — U128/U256/S128/F128/Decimal/Address/…). It serializes as a single
        // Text value: its `represent()` boxed into the Text arm. The OLD behavior field-walked it into a
        // `Dict[Text, SerialValue]`; with zero fields that Dict was degenerate and its `Dict.create`
        // never monomorphized, leaking the generic `Core.Dict.create` into codegen (the synth-body-failed
        // warning flood). A structured type with ≥1 field (even a single-field record like `Leaf{x}`)
        // still field-walks into the Dict arm below, preserving `{x: …}` structure. A specific type may
        // hand-define `serialize()` to override.
        if (fields.Count == 0)
        {
            var meRepr = new IdentifierExpression(Name: "me", Location: _synthLoc)
            {
                ResolvedType = owner
            };
            var reprCall = new CallExpression(
                Callee: new MemberExpression(Object: meRepr,
                    MemberName: RepresentMemberRoutineName,
                    Location: _synthLoc) { ResolvedType = textType },
                Arguments: [],
                Location: _synthLoc) { ResolvedType = textType };
            var boxedText =
                new CreatorExpression(TypeName: serialValue.Name,
                    TypeArguments: null,
                    MemberVariables: [("Text", reprCall)],
                    Location: _synthLoc)
                {
                    ResolvedType = serialValue, ConstructedType = serialValue
                };
            return new ReturnStatement(Value: boxedText, Location: _synthLoc);
        }

        // The Dict[Text, SerialValue] arm is the only 2-type-argument member; use its resolved Type
        // (and Name) directly so the DictLiteral type and the boxing arm are the exact same resolution.
        VariantMemberInfo? dictArm = serialValue.Members.FirstOrDefault(predicate: m =>
            m.Type?.TypeArguments is { Count: 2 });
        if (dictArm?.Type == null) return null;
        TypeInfo dictType = dictArm.Type;

        var pairs = new List<(Expression Key, Expression Value)>();
        foreach (MemberVariableInfo field in fields)
        {
            var keyLit = new LiteralExpression(Value: field.Name,
                LiteralType: TokenType.TextLiteral,
                Location: _synthLoc) { ResolvedType = textType };
            var meField = new MemberExpression(
                Object: new IdentifierExpression(Name: "me", Location: _synthLoc)
                {
                    ResolvedType = owner
                },
                MemberName: field.Name,
                Location: _synthLoc) { ResolvedType = field.Type };
            pairs.Add(item: (keyLit,
                BuildSerializeMemberVariableValue(field: field,
                    meField: meField,
                    serialValue: serialValue,
                    textType: textType)));
        }

        var dict = new DictLiteralExpression(Pairs: pairs,
            KeyType: null,
            ValueType: null,
            Location: _synthLoc) { ResolvedType = dictType };
        var boxed =
            new CreatorExpression(TypeName: serialValue.Name,
                TypeArguments: null,
                MemberVariables: [(dictArm.Name, dict)],
                Location: _synthLoc) { ResolvedType = serialValue, ConstructedType = serialValue };
        return new ReturnStatement(Value: boxed, Location: _synthLoc);
    }

    private Expression BuildSerializeMemberVariableValue(MemberVariableInfo field, Expression meField,
        VariantTypeInfo serialValue, TypeInfo textType)
    {
        // Direct SerialValue arm (S8..U64 / F32/F64 / Bool / Moment / Bytes / Text) -> box inline.
        VariantMemberInfo? arm = FindScalarArm(serialValue: serialValue, fieldType: field.Type);
        if (arm != null)
            return new CreatorExpression(TypeName: serialValue.Name,
                TypeArguments: null,
                MemberVariables: [(arm.Type!.Name, meField)],
                Location: _synthLoc) { ResolvedType = serialValue, ConstructedType = serialValue };

        // Aggregate with a REAL synthesized serialize() (not an @llvm primitive record) -> recurse.
        bool recurse = field.Type switch
        {
            RecordTypeInfo r => r.BackendType == null && TypeHasSerialize(type: r),
            EntityTypeInfo e => TypeHasSerialize(type: e),
            _ => false,
        };
        if (recurse)
            return new CallExpression(
                Callee: new MemberExpression(Object: meField,
                    MemberName: SerializeMemberRoutineName,
                    Location: _synthLoc) { ResolvedType = serialValue },
                Arguments: [],
                Location: _synthLoc) { ResolvedType = serialValue };

        // Fallback: Text(field.represent()). Routine-typed fields have no represent -> placeholder.
        Expression textVal = field.Type is RoutineTypeInfo
            ? new LiteralExpression(Value: "<routine>",
                LiteralType: TokenType.TextLiteral,
                Location: _synthLoc) { ResolvedType = textType }
            : new CallExpression(
                Callee: new MemberExpression(Object: meField,
                    MemberName: RepresentMemberRoutineName,
                    Location: _synthLoc) { ResolvedType = textType },
                Arguments: [],
                Location: _synthLoc) { ResolvedType = textType };
        return new CreatorExpression(TypeName: serialValue.Name,
            TypeArguments: null,
            MemberVariables: [("Text", textVal)],
            Location: _synthLoc) { ResolvedType = serialValue, ConstructedType = serialValue };
    }

    private static VariantMemberInfo? FindScalarArm(VariantTypeInfo serialValue,
        TypeInfo fieldType)
    {
        foreach (VariantMemberInfo m in serialValue.Members)
        {
            if (m.IsNone || m.Type is null) continue;
            // List[SerialValue] / Dict[Text, SerialValue] arms are generic (recursion arms), not scalars.
            if (m.Type.TypeArguments is { Count: > 0 }) continue;
            if (m.Type.Name == fieldType.Name || m.Type.FullName == fieldType.FullName) return m;
        }

        return null;
    }

    private bool TypeHasSerialize(TypeInfo type) =>
        // LookupMemberRoutine resolves through the generic definition, so a concrete instance like
        // `List[S32]` sees the generic `List[T].serialize` (GetMemberRoutinesForType only lists the instance's
        // own already-materialized memberRoutines, which misses it during field-value synthesis).
        ctx.Registry.LookupMemberRoutine(type: type, memberRoutineName: SerializeMemberRoutineName) is not null || ctx.Registry
           .GetMemberRoutinesForType(type: type)
           .Any(predicate: m => m.Name == SerializeMemberRoutineName);

    //  represent / diagnose (choice)

    /// <summary>
    /// Builds the body: a WhenStatement over <c>me</c> returning the case name string.
    /// </summary>
    private static WhenStatement BuildChoiceRepresentBody(ChoiceTypeInfo choice, TypeInfo textType,
        TypeInfo? logicBreachedErrorType)
    {
        var meRef = new IdentifierExpression(Name: "me", Location: _synthLoc)
        {
            ResolvedType = choice
        };

        var clauses = new List<WhenClause>(capacity: choice.Cases.Count + 1);
        foreach (ChoiceCaseInfo c in choice.Cases)
        {
            clauses.Add(new WhenClause(
                Pattern: new LiteralPattern(Value: c.ComputedValue,
                    LiteralType: TokenType.S32Literal,
                    Location: _synthLoc),
                Body: new ReturnStatement(
                    Value: new LiteralExpression(Value: c.Name,
                        LiteralType: TokenType.TextLiteral,
                        Location: _synthLoc) { ResolvedType = textType },
                    Location: _synthLoc),
                Location: _synthLoc));
        }

        clauses.Add(new WhenClause(
            Pattern: new ElsePattern(VariableName: null, Location: _synthLoc),
            Body: BuildBreachStatement(logicBreachedErrorType: logicBreachedErrorType),
            Location: _synthLoc));

        return new WhenStatement(Expression: meRef, Clauses: clauses, Location: _synthLoc);
    }

    /// <summary>
    /// Builds the body: a WhenStatement over <c>me</c> returning
    /// <c>"Module.ChoiceName(id: N, CaseName)"</c> per case.
    /// </summary>
    private static WhenStatement BuildChoiceDiagnoseBody(ChoiceTypeInfo choice, TypeInfo textType,
        TypeInfo? logicBreachedErrorType)
    {
        var meRef = new IdentifierExpression(Name: "me", Location: _synthLoc)
        {
            ResolvedType = choice
        };

        string prefix = choice.FullName + "(id: ";
        var clauses = new List<WhenClause>(capacity: choice.Cases.Count + 1);
        foreach (ChoiceCaseInfo c in choice.Cases)
        {
            string text = $"{prefix}{c.ComputedValue}, {c.Name})";
            clauses.Add(new WhenClause(
                Pattern: new LiteralPattern(Value: c.ComputedValue,
                    LiteralType: TokenType.S32Literal,
                    Location: _synthLoc),
                Body: new ReturnStatement(
                    Value: new LiteralExpression(Value: text,
                        LiteralType: TokenType.TextLiteral,
                        Location: _synthLoc) { ResolvedType = textType },
                    Location: _synthLoc),
                Location: _synthLoc));
        }

        clauses.Add(new WhenClause(
            Pattern: new ElsePattern(VariableName: null, Location: _synthLoc),
            Body: BuildBreachStatement(logicBreachedErrorType: logicBreachedErrorType),
            Location: _synthLoc));

        return new WhenStatement(Expression: meRef, Clauses: clauses, Location: _synthLoc);
    }

    //  represent / diagnose (flags)

    private void HandleFlags(RoutineInfo routine, FlagsTypeInfo flags, TypeInfo textType,
        TypeInfo boolType, TypeInfo? u64Type, TypeInfo? listTypeDef)
    {
        switch (routine.Name)
        {
            // eq/ne emit `int_eq`/`int_ne` on the underlying i64 repr directly (not `me == you`), so
            // codegen needs no Flags cmp special-case and OperatorLoweringPass — which lowers `==` to
            // `.eq()` — never recurses back into this body. ne is derived from eq by DerivedOperatorPass.
            case "eq" when u64Type != null:
                ctx.VariantBodies[key: routine.RegistryKey] = BuildLlvmBinaryIntrinsicCallBody(
                    intrinsicName: "int_eq", ownerType: flags, reprType: u64Type,
                    resultType: boolType);
                break;

            // Bitwise combinators via `bit_or`/`bit_and`/`bit_xor` on the i64 repr — no codegen
            // Flags-bitwise special-case, no operator recursion.
            case "bitor" when u64Type != null:
                ctx.VariantBodies[key: routine.RegistryKey] = BuildLlvmBinaryIntrinsicCallBody(
                    intrinsicName: "bit_or", ownerType: flags, reprType: u64Type, resultType: flags);
                break;
            case "bitand" when u64Type != null:
                ctx.VariantBodies[key: routine.RegistryKey] = BuildLlvmBinaryIntrinsicCallBody(
                    intrinsicName: "bit_and", ownerType: flags, reprType: u64Type, resultType: flags);
                break;
            case "bitxor" when u64Type != null:
                ctx.VariantBodies[key: routine.RegistryKey] = BuildLlvmBinaryIntrinsicCallBody(
                    intrinsicName: "bit_xor", ownerType: flags, reprType: u64Type, resultType: flags);
                break;

            case HashMemberRoutineName when u64Type != null && routine.Parameters.Count == 0:
                ctx.VariantBodies[key: routine.RegistryKey] = BuildNumericHashBodyViaConversion(
                    ownerType: flags,
                    conversionTypeName: "U64",
                    conversionType: u64Type,
                    u64Type: u64Type);
                break;

            case HashMemberRoutineName when u64Type != null && routine.Parameters.Count == 2:
                ctx.VariantBodies[key: routine.RegistryKey] =
                    BuildNumericSecureHashBodyViaConversion(ownerType: flags,
                        conversionTypeName: "U64",
                        conversionType: u64Type,
                        u64Type: u64Type);
                break;

            case "all_cases":
                // Cloned from the `@overridable … needs T is FlagsType` DeriveText template (caseof unroll
                // reconstructing each member via `Me(from: $valueof(c))`); C# BuildAllCasesBody is the fallback.
                ctx.VariantBodies[key: routine.RegistryKey] =
                    CloneUniversalDeriveBody(ownerType: flags, synthesized: routine,
                        memberRoutineName: "all_cases")
                    ?? (listTypeDef != null
                        ? BuildAllCasesBody(
                            memberNames: flags.Members.Select(m => m.Name).ToList(),
                            elementType: flags,
                            listType: ctx.Registry.GetOrCreateResolution(genericDef: listTypeDef,
                                typeArguments: [flags]))
                        : null!);
                break;

            case "count" when u64Type != null:
                // Buildtime constant: the declared member count is known here, so emit `return <N>_u64`
                // directly instead of a `caseof` unroll that increments N times at runtime.
                ctx.VariantBodies[key: routine.RegistryKey] =
                    BuildCountBody(count: flags.Members.Count, u64Type: u64Type);
                break;

            case CreateMemberRoutineName
                when !routine.IsFailable && routine.Parameters is [{ Name: "from" }]:
                // Reverse constructor `Flags.create(from: U64)` — reinterpret the bitmask bits.
                ctx.VariantBodies[key: routine.RegistryKey] = BuildLlvmIntrinsicCallBody(
                    intrinsicName: "reinterpret_bits",
                    fromType: routine.Parameters[0].Type!,
                    toType: flags,
                    paramName: "from");
                break;

            case RepresentMemberRoutineName:
                // Cloned from the `@override … needs T is FlagsType` derive template (SUBSET
                // accumulation via `caseof`); falls back to the C# builder.
                ctx.VariantBodies[key: routine.RegistryKey] =
                    CloneUniversalDeriveBody(ownerType: flags, synthesized: routine,
                        memberRoutineName: RepresentMemberRoutineName)
                    ?? BuildFlagsRepresentBody(flags: flags, textType: textType, boolType: boolType);
                break;

            case DiagnoseMemberRoutineName:
                ctx.VariantBodies[key: routine.RegistryKey] =
                    CloneUniversalDeriveBody(ownerType: flags, synthesized: routine,
                        memberRoutineName: DiagnoseMemberRoutineName)
                    ?? BuildFlagsDiagnoseBody(flags: flags, textType: textType, boolType: boolType);
                break;

            case SerializeMemberRoutineName when !flags.IsGenericDefinition:
                // A `flags` mask boxes its `represent()` Text (zero-field BuildSerializeBody path) — the
                // universal-serialize fallback that replaces the composite derive's `obeying` else-branch.
                ctx.VariantBodies[key: routine.RegistryKey] =
                    BuildSerializeBody(owner: flags, fields: [], textType: textType);
                break;

            case "all_off":
                // 0UL so the literal is U64 (flags are U64-backed), not an S64 literal in a U64 slot.
                ctx.VariantBodies[key: routine.RegistryKey] = MakeLiteralReturn(value: 0UL,
                    returnType: routine.ReturnType ?? flags);
                break;

            case "all_on":
            {
                ulong mask = 0;
                foreach (FlagsMemberInfo member in flags.Members)
                    mask |= 1UL << member.BitPosition;
                ctx.VariantBodies[key: routine.RegistryKey] = MakeLiteralReturn(
                    value: unchecked((long)mask),
                    returnType: routine.ReturnType ?? flags);
                break;
            }

            case "assign":
                ctx.VariantBodies[key: routine.RegistryKey] = BuildReturnMeBody(ownerType: flags);
                break;

            case "duplicate":
                ctx.VariantBodies[key: routine.RegistryKey] =
                    BuildCloneViaCopyBody(ownerType: flags);
                break;
        }
    }

    /// <summary>
    /// Builds the common block of statements that computes:
    /// <list type="bullet">
    ///   <item><c>var result: Text = ""</c> -> flag names joined by <c>" and "</c>.</item>
    ///   <item><c>var first: Bool = true</c> -> separator sentinel.</item>
    ///   <item>When <paramref name="computeBits"/> is <c>true</c>: <c>var bits: Text = "%"</c> ??
    ///         binary string in declaration order, e.g. <c>"%110"</c>.</item>
    ///   <item>For each flag in declaration order:
    ///     <c>if (me &amp; mask) != 0 { /* append name to result; first = false; [append "1" to bits] */ }
    ///     else { [append "0" to bits] }</c></item>
    ///   <item><c>if first { result = "&lt;none&gt;" }</c></item>
    /// </list>
    /// Returns the statement list (no trailing <c>return</c>).
    /// </summary>
    private static List<Statement> BuildFlagsComputeBlock(FlagsTypeInfo flags, TypeInfo textType,
        TypeInfo boolType, TypeInfo s64Type, bool computeBits)
    {
        var stmts = new List<Statement>();
        var emptyText = new LiteralExpression(
            Value: "",
            LiteralType: TokenType.TextLiteral,
            Location: _synthLoc) { ResolvedType = textType };
        var trueLit =
            new LiteralExpression(Value: true, LiteralType: TokenType.True, Location: _synthLoc)
            {
                ResolvedType = boolType
            };
        var falseLit = new LiteralExpression(
            Value: false,
            LiteralType: TokenType.False,
            Location: _synthLoc) { ResolvedType = boolType };
        var zeroLit = new LiteralExpression(
            Value: 0L,
            LiteralType: TokenType.S64Literal,
            Location: _synthLoc) { ResolvedType = s64Type };
        var noneLit = new LiteralExpression(
            Value: "<none>",
            LiteralType: TokenType.TextLiteral,
            Location: _synthLoc) { ResolvedType = textType };
        var oneLit = new LiteralExpression(
            Value: "1",
            LiteralType: TokenType.TextLiteral,
            Location: _synthLoc) { ResolvedType = textType };
        var zeroCharLit = new LiteralExpression(
            Value: "0",
            LiteralType: TokenType.TextLiteral,
            Location: _synthLoc) { ResolvedType = textType };

        // var result: Text = ""
        stmts.Add(new DeclarationStatement(
            Declaration: new VariableDeclaration(Name: ResultVarName,
                Type: null,
                Initializer: emptyText,
                Visibility: VisibilityModifier.Open,
                Location: _synthLoc),
            Location: _synthLoc));

        // var first: Bool = true
        stmts.Add(new DeclarationStatement(Declaration: new VariableDeclaration(Name: FirstVarName,
                Type: null,
                Initializer: trueLit,
                Visibility: VisibilityModifier.Open,
                Location: _synthLoc),
            Location: _synthLoc));

        if (computeBits)
        {
            // var bits: Text = "%"
            stmts.Add(new DeclarationStatement(Declaration: new VariableDeclaration(Name: "bits",
                    Type: null,
                    Initializer: new LiteralExpression(Value: "%",
                        LiteralType: TokenType.TextLiteral,
                        Location: _synthLoc) { ResolvedType = textType },
                    Visibility: VisibilityModifier.Open,
                    Location: _synthLoc),
                Location: _synthLoc));
        }

        var meRef = new IdentifierExpression(Name: "me", Location: _synthLoc)
        {
            ResolvedType = flags
        };

        foreach (FlagsMemberInfo member in flags.Members)
        {
            long mask = 1L << member.BitPosition;
            var maskLit = new LiteralExpression(
                Value: mask,
                LiteralType: TokenType.S64Literal,
                Location: _synthLoc) { ResolvedType = s64Type };

            // (me & mask) != 0
            var bwAnd = new BinaryExpression(Left: meRef,
                Operator: BinaryOperator.BitwiseAnd,
                Right: maskLit,
                Location: _synthLoc) { ResolvedType = s64Type };
            var isSet = new BinaryExpression(
                Left: bwAnd,
                Operator: BinaryOperator.NotEqual,
                Right: zeroLit,
                Location: _synthLoc) { ResolvedType = boolType };

            var nameLit = new LiteralExpression(
                Value: member.Name,
                LiteralType: TokenType.TextLiteral,
                Location: _synthLoc) { ResolvedType = textType };
            var andNameLit = new LiteralExpression(Value: " and " + member.Name,
                LiteralType: TokenType.TextLiteral,
                Location: _synthLoc) { ResolvedType = textType };

            // result.add(other: " and FlagName")
            var appendNameCall = new CallExpression(
                Callee: new MemberExpression(
                    Object: new IdentifierExpression(Name: ResultVarName, Location: _synthLoc)
                    {
                        ResolvedType = textType
                    },
                    MemberName: "add",
                    Location: _synthLoc),
                Arguments:
                [
                    new NamedArgumentExpression(Name: OtherParamName,
                        Value: andNameLit,
                        Location: _synthLoc)
                ],
                Location: _synthLoc) { ResolvedType = textType };

            // if first { result = "FlagName"; first = false } else { result = result.add(...) }
            var innerNameIf = new IfStatement(
                Condition: new IdentifierExpression(Name: FirstVarName, Location: _synthLoc)
                {
                    ResolvedType = boolType
                },
                ThenStatement: new BlockStatement(Statements:
                    [
                        new AssignmentStatement(
                            Target: new IdentifierExpression(Name: ResultVarName,
                                Location: _synthLoc)
                            {
                                ResolvedType = textType
                            },
                            Value: nameLit,
                            Location: _synthLoc),
                        new AssignmentStatement(
                            Target: new IdentifierExpression(Name: FirstVarName,
                                Location: _synthLoc)
                            {
                                ResolvedType = boolType
                            },
                            Value: falseLit,
                            Location: _synthLoc)
                    ],
                    Location: _synthLoc),
                ElseStatement: new BlockStatement(Statements:
                    [
                        new AssignmentStatement(
                            Target: new IdentifierExpression(Name: ResultVarName,
                                Location: _synthLoc)
                            {
                                ResolvedType = textType
                            },
                            Value: appendNameCall,
                            Location: _synthLoc)
                    ],
                    Location: _synthLoc),
                Location: _synthLoc);

            if (!computeBits)
            {
                // if (me & mask) != 0 { <name logic> }
                stmts.Add(new IfStatement(Condition: isSet,
                    ThenStatement: new BlockStatement(Statements:
                        [innerNameIf],
                        Location: _synthLoc),
                    ElseStatement: null,
                    Location: _synthLoc));
            }
            else
            {
                // bits.add(other: "1") -> set branch
                var append1 = new CallExpression(
                    Callee: new MemberExpression(
                        Object: new IdentifierExpression(Name: "bits", Location: _synthLoc)
                        {
                            ResolvedType = textType
                        },
                        MemberName: "add",
                        Location: _synthLoc),
                    Arguments:
                    [
                        new NamedArgumentExpression(Name: OtherParamName,
                            Value: oneLit,
                            Location: _synthLoc)
                    ],
                    Location: _synthLoc) { ResolvedType = textType };

                // bits.add(other: "0") -> clear branch
                var append0 = new CallExpression(
                    Callee: new MemberExpression(
                        Object: new IdentifierExpression(Name: "bits", Location: _synthLoc)
                        {
                            ResolvedType = textType
                        },
                        MemberName: "add",
                        Location: _synthLoc),
                    Arguments:
                    [
                        new NamedArgumentExpression(Name: OtherParamName,
                            Value: zeroCharLit,
                            Location: _synthLoc)
                    ],
                    Location: _synthLoc) { ResolvedType = textType };

                // if (me & mask) != 0 { <name logic>; bits = bits.add("1") }
                // else               { bits = bits.add("0") }
                stmts.Add(new IfStatement(Condition: isSet,
                    ThenStatement: new BlockStatement(Statements:
                        [
                            innerNameIf,
                            new AssignmentStatement(
                                Target: new IdentifierExpression(Name: "bits", Location: _synthLoc)
                                {
                                    ResolvedType = textType
                                },
                                Value: append1,
                                Location: _synthLoc)
                        ],
                        Location: _synthLoc),
                    ElseStatement: new BlockStatement(Statements:
                        [
                            new AssignmentStatement(
                                Target: new IdentifierExpression(Name: "bits", Location: _synthLoc)
                                {
                                    ResolvedType = textType
                                },
                                Value: append0,
                                Location: _synthLoc)
                        ],
                        Location: _synthLoc),
                    Location: _synthLoc));
            }
        }

        // if first { result = "<none>" }
        stmts.Add(new IfStatement(
            Condition: new IdentifierExpression(Name: FirstVarName, Location: _synthLoc)
            {
                ResolvedType = boolType
            },
            ThenStatement: new BlockStatement(Statements:
                [
                    new AssignmentStatement(
                        Target: new IdentifierExpression(Name: ResultVarName, Location: _synthLoc)
                        {
                            ResolvedType = textType
                        },
                        Value: noneLit,
                        Location: _synthLoc)
                ],
                Location: _synthLoc),
            ElseStatement: null,
            Location: _synthLoc));

        return stmts;
    }

    /// <summary>
    /// Builds the <c>represent</c> body for a flags type.
    /// Returns <c>"Flag1 and Flag2"</c>, or <c>"&lt;none&gt;"</c> if no bits are set.
    /// </summary>
    private Statement BuildFlagsRepresentBody(FlagsTypeInfo flags, TypeInfo textType,
        TypeInfo boolType)
    {
        TypeInfo? s64Type = ctx.Registry.LookupType(name: "S64");
        if (s64Type == null)
            return new ReturnStatement(
                Value: new LiteralExpression(Value: "<none>",
                    LiteralType: TokenType.TextLiteral,
                    Location: _synthLoc) { ResolvedType = textType },
                Location: _synthLoc);

        List<Statement> stmts = BuildFlagsComputeBlock(flags: flags,
            textType: textType,
            boolType: boolType,
            s64Type: s64Type,
            computeBits: false);

        stmts.Add(new ReturnStatement(
            Value: new IdentifierExpression(Name: ResultVarName, Location: _synthLoc)
            {
                ResolvedType = textType
            },
            Location: _synthLoc));

        return new BlockStatement(Statements: stmts, Location: _synthLoc);
    }

    /// <summary>
    /// Builds the <c>diagnose</c> body for a flags type.
    /// Returns <c>"Module.FlagsName(value: %110, Flag1 and Flag2)"</c> where the binary string
    /// is in declaration order (<c>%</c> prefix, leftmost = first declared flag).
    /// </summary>
    private Statement BuildFlagsDiagnoseBody(FlagsTypeInfo flags, TypeInfo textType,
        TypeInfo boolType)
    {
        TypeInfo? s64Type = ctx.Registry.LookupType(name: "S64");
        if (s64Type == null)
            return new ReturnStatement(
                Value: new LiteralExpression(Value: flags.FullName + "(value: %0, <none>)",
                    LiteralType: TokenType.TextLiteral,
                    Location: _synthLoc) { ResolvedType = textType },
                Location: _synthLoc);

        List<Statement> stmts = BuildFlagsComputeBlock(flags: flags,
            textType: textType,
            boolType: boolType,
            s64Type: s64Type,
            computeBits: true);

        // return f"Module.FlagsName(value: {bits}, {result})"
        // Both result and bits are Text -> EmitRepresentCall returns them directly.
        var resultRef = new IdentifierExpression(Name: ResultVarName, Location: _synthLoc)
        {
            ResolvedType = textType
        };
        var bitsRef = new IdentifierExpression(Name: "bits", Location: _synthLoc)
        {
            ResolvedType = textType
        };
        var fstring = new InsertedTextExpression(Parts:
            [
                new TextPart(Text: flags.FullName + "(value: ", Location: _synthLoc),
                new ExpressionPart(Expression: bitsRef, FormatSpec: null, Location: _synthLoc),
                new TextPart(Text: ", ", Location: _synthLoc),
                new ExpressionPart(Expression: resultRef, FormatSpec: null, Location: _synthLoc),
                new TextPart(Text: ")", Location: _synthLoc)
            ],
            IsRaw: false,
            Location: _synthLoc) { ResolvedType = textType };

        stmts.Add(new ReturnStatement(Value: fstring, Location: _synthLoc));

        return new BlockStatement(Statements: stmts, Location: _synthLoc);
    }

    //  Unreachable helper

    /// <summary>
    /// Builds <c>throw LogicBreachedError()</c> for provably-unreachable else arms
    /// (e.g. the default clause in a synthesized choice <c>represent</c> body).
    /// Falls back to <c>throw LogicBreachedError()</c> with a null ResolvedType when the
    /// type isn't in the registry yet (shouldn't happen in practice).
    /// </summary>
    private static ThrowStatement BuildBreachStatement(TypeInfo? logicBreachedErrorType)
    {
        // Use CallExpression, not CreatorExpression -> crashable constructors in RF source
        // parse as calls (e.g. LogicBreachedError()), and EmitFunctionCall has the
        // crashable-construction path; EmitConstructorCall does not.
        var call = new CallExpression(
            Callee: new IdentifierExpression(Name: "LogicBreachedError", Location: _synthLoc)
            {
                ResolvedType = logicBreachedErrorType
            },
            Arguments: [],
            Location: _synthLoc) { ResolvedType = logicBreachedErrorType };
        return new ThrowStatement(Error: call, Location: _synthLoc);
    }

    //  represent / diagnose (crashable)

    /// <summary>
    /// Builds the body: <c>return me.crash_message()</c>.
    /// </summary>
    private static ReturnStatement BuildCrashableRepresentBody(CrashableTypeInfo crashable)
    {
        var meRef = new IdentifierExpression(Name: "me", Location: _synthLoc)
        {
            ResolvedType = crashable
        };
        var call = new CallExpression(Callee: new MemberExpression(Object: meRef,
                MemberName: Declaration.RuntimeContract.CrashMessage,
                Location: _synthLoc),
            Arguments: [],
            Location: _synthLoc);
        return new ReturnStatement(Value: call, Location: _synthLoc);
    }

    /// <summary>
    /// Builds the DEFAULT crash_message body — <c>return me.crash_title()</c> — used for a crashable
    /// that declares no explicit crash_message. Overridden by a user-declared one.
    /// </summary>
    private static ReturnStatement BuildCrashableCrashMessageBody(CrashableTypeInfo crashable)
    {
        var meRef = new IdentifierExpression(Name: "me", Location: _synthLoc)
        {
            ResolvedType = crashable
        };
        var call = new CallExpression(Callee: new MemberExpression(Object: meRef,
                MemberName: "crash_title",
                Location: _synthLoc),
            Arguments: [],
            Location: _synthLoc);
        return new ReturnStatement(Value: call, Location: _synthLoc);
    }

    /// <summary>
    /// Builds the body:
    /// <c>return f"Module.CrashableName({me.crash_message()}[, field1: {me.f1}, ...])"</c>.
    /// </summary>
    private static ReturnStatement BuildCrashableDiagnoseBody(CrashableTypeInfo crashable,
        TypeInfo textType)
    {
        var parts = new List<InsertedTextPart>();

        // Open with "Module.TypeName("
        parts.Add(new TextPart(Text: crashable.FullName + "(", Location: _synthLoc));

        // First element: crash_message() -> use represent format (no "?")
        var meRef = new IdentifierExpression(Name: "me", Location: _synthLoc)
        {
            ResolvedType = crashable
        };
        var crashMsgCall = new CallExpression(Callee: new MemberExpression(Object: meRef,
                MemberName: Declaration.RuntimeContract.CrashMessage,
                Location: _synthLoc),
            Arguments: [],
            Location: _synthLoc);
        parts.Add(new ExpressionPart(Expression: crashMsgCall,
            FormatSpec: null,
            Location: _synthLoc));

        // Remaining member-variable fields
        foreach (MemberVariableInfo field in crashable.MemberVariables)
        {
            parts.Add(new TextPart(Text: ", " + field.Name + ": ", Location: _synthLoc));

            var meRef2 = new IdentifierExpression(Name: "me", Location: _synthLoc)
            {
                ResolvedType = crashable
            };
            var fieldExpr =
                new MemberExpression(Object: meRef2, MemberName: field.Name, Location: _synthLoc)
                {
                    ResolvedType = field.Type
                };

            parts.Add(new ExpressionPart(Expression: fieldExpr,
                FormatSpec: null,
                Location: _synthLoc));
        }

        parts.Add(new TextPart(Text: ")", Location: _synthLoc));

        var fstring =
            new InsertedTextExpression(Parts: parts, IsRaw: false, Location: _synthLoc)
            {
                ResolvedType = textType
            };

        return new ReturnStatement(Value: fstring, Location: _synthLoc);
    }

    //  BuilderQuery constant routines

    /// <summary>
    /// Synthesizes AST bodies for BuilderQuery routines that return a single compile-time
    /// constant value (Text, U64, S64, Bool). Called before the owner-type switch so it handles
    /// all types uniformly.
    /// Returns <c>true</c> if the routine was handled, <c>false</c> otherwise.
    /// </summary>
    private bool TryHandleBuilderQueryConstant(RoutineInfo routine, TypeInfo textType,
        TypeInfo? u64Type, TypeInfo? s64Type, TypeInfo? boolType,
        TypeInfo? typeKindType, TypeInfo? listTextType,
        TypeInfo? byteSizeType = null)
    {
        if (routine.OwnerType == null) return false;
        TypeInfo owner = routine.OwnerType;

        // Skip compiler-internal/non-synthesizable categories.
        if (owner.Category is TypeCategory.TypeParameter or TypeCategory.Error
            or TypeCategory.ProtocolSelf or TypeCategory.ConstGenericValue)
            return false;

        // Fold-only constants (type_name/data_size/type_id/...): BuilderQueryInliningPass
        // and GenericAstRewriter fold EVERY call site to a literal computed from TypeInfo, so
        // a synthesized body is pure dead weight in the emitted IR — and for generic-def
        // owners it would bake the unparameterized name ("List" instead of "List[S64]").
        // Register no body: nothing related to these may survive the inlining pass. An
        // unfolded call site surfacing as a linker error indicates a folding bug to fix
        // at the pass layer, not a missing definition.
        if (BuilderQueryInliningPass.IsFoldable(routineName: routine.Name))
            return true;

        // List-returning per-type CONSTANT reflection (routine_names/protocols/generic_args/annotations/
        // dependencies): 0 runtime params, value is a compile-time-constant List[Text] of the owner type.
        // Do NOT synthesize a routine body — like the scalar foldables above, these are folded at the call
        // site (SemanticVerifier.FoldListBuilderQueryReflection, before reachability) to an inline analyzed
        // list literal. A synthesized `return List.from_literal([...])` body would otherwise survive as a
        // DEAD routine that the non-pruned resident-JIT base emits, dangling an unmaterialized
        // from_literal(Array[Text,N]) (RESIDENT-JIT-INCREMENTAL-V0.5.md ★ / dj-lumiere: BuilderQuery must
        // not survive desugaring as routines). Non-constant BuilderQuery members (member_type_id/
        // protocol_info/routine_info/… — runtime params or entity-list builders) keep their real bodies below.
        if (BuilderInfoProvider.IsListReturningConstantRoutine(name: routine.Name))
            return true;

        switch (routine.Name)
        {
            case "member_type_id" when u64Type != null && boolType != null:
                ctx.VariantBodies[key: routine.RegistryKey] = BuildMemberTypeIdBody(owner: owner,
                    textType: textType, u64Type: u64Type, boolType: boolType);
                return true;

            case "protocols" when listTextType != null:
            {
                List<string> names = owner switch
                {
                    RecordTypeInfo r => r.ImplementedProtocols
                                         .Select(p => p.Name)
                                         .ToList(),
                    EntityTypeInfo e => e.ImplementedProtocols
                                         .Select(p => p.Name)
                                         .ToList(),
                    _ => []
                };
                ctx.VariantBodies[key: routine.RegistryKey] = MakeListReturn(values: names,
                    textType: textType,
                    listTextType: listTextType);
                return true;
            }

            case "routine_names" when listTextType != null:
            {
                var names = ctx.Registry
                               .GetMemberRoutinesForType(type: owner)
                               .Select(r => r.Name)
                               .Distinct()
                               .ToList();
                ctx.VariantBodies[key: routine.RegistryKey] = MakeListReturn(values: names,
                    textType: textType,
                    listTextType: listTextType);
                return true;
            }

            case "generic_args" when listTextType != null:
            {
                List<string> args = owner.TypeArguments
                                        ?.Select(t => t.Name)
                                         .ToList() ?? owner.GenericParameters?.ToList() ?? [];
                ctx.VariantBodies[key: routine.RegistryKey] = MakeListReturn(values: args,
                    textType: textType,
                    listTextType: listTextType);
                return true;
            }

            case "annotations" when listTextType != null:
                // Type-level annotations are plumbed onto TypeInfo.Annotations from the declaration
                // (records/protocols can be annotated; entities/choices/etc. carry none -> empty list).
                ctx.VariantBodies[key: routine.RegistryKey] = MakeListReturn(
                    values: owner.Annotations ?? [],
                    textType: textType,
                    listTextType: listTextType);
                return true;

            case "dependencies" when listTextType != null:
                // Modules the owner type's declaring module imports (recorded after the Phase-3
                // declaration sweep). Empty for stdlib/builtin owners whose module has no tracked imports.
                ctx.VariantBodies[key: routine.RegistryKey] = MakeListReturn(
                    values: ctx.Registry.GetModuleDependencies(module: owner.Module).ToList(),
                    textType: textType,
                    listTextType: listTextType);
                return true;

            // protocol_info / routine_info build entities whose fields include an OWNED nested collection
            // (ProtocolInfo.routine_names, RoutineInfo.param_types/param_names — all List[Text]). The
            // hoisted nested-list temp is MOVED into the entity creator; ScopeTeardownLoweringPass's
            // entity-creator move-detection (added for exactly this) keeps it from being double-freed.
            case "protocol_info"
                when owner is RecordTypeInfo or EntityTypeInfo && boolType != null && listTextType != null:
                return TryBuildProtocolInfoBody(routine: routine, owner: owner, textType: textType,
                    boolType: boolType, listTextType: listTextType);

            case "routine_info"
                when owner is RecordTypeInfo or EntityTypeInfo && boolType != null && listTextType != null:
                return TryBuildRoutineInfoBody(routine: routine, owner: owner, textType: textType,
                    boolType: boolType, listTextType: listTextType);

            case "member_variable_info"
                when owner is RecordTypeInfo or EntityTypeInfo:
                return TryBuildMemberVariableInfoBody(routine: routine, owner: owner,
                    textType: textType, u64Type: u64Type);

            default:
                return false;
        }
    }

    /// <summary>
    /// Builds the <c>member_type_id(member_name)</c> body: an if-elseif chain from last field to
    /// first (so the outermost IfStatement checks field[0]), each arm returning that field's type id;
    /// the fallback returns <c>0_u64</c>.
    /// </summary>
    private Statement BuildMemberTypeIdBody(TypeInfo owner, TypeInfo textType, TypeInfo u64Type,
        TypeInfo boolType)
    {
        List<MemberVariableInfo>? fields = owner switch
        {
            RecordTypeInfo r => r.MemberVariables,
            EntityTypeInfo e => e.MemberVariables,
            _ => null
        };
        fields ??= [];

        // Build if-elseif chain from last field to first, wrapping each around the
        // previous so the outermost IfStatement checks field[0].
        // `0UL` (not `0L`) so the fallback literal is U64, matching the U64 return type — the
        // long overload would emit an S64 literal in a U64 routine.
        Statement body = MakeLiteralReturn(value: 0UL, returnType: u64Type);
        var memberNameRef =
            new IdentifierExpression(Name: "member_name", Location: _synthLoc)
            {
                ResolvedType = textType
            };

        for (int i = fields.Count - 1; i >= 0; i--)
        {
            MemberVariableInfo field = fields[i];
            ulong typeId = TypeIdHelper.ComputeTypeId(fullName: field.Type.FullName);

            Expression cond = new CallExpression(
                Callee: new MemberExpression(Object: memberNameRef,
                    MemberName: "eq",
                    Location: _synthLoc),
                Arguments:
                [
                    new NamedArgumentExpression(Name: OtherParamName,
                        Value: new LiteralExpression(Value: field.Name,
                            LiteralType: TokenType.TextLiteral,
                            Location: _synthLoc) { ResolvedType = textType },
                        Location: _synthLoc)
                ],
                Location: _synthLoc) { ResolvedType = boolType };

            body = new IfStatement(Condition: cond,
                ThenStatement: new ReturnStatement(
                    Value: new LiteralExpression(Value: typeId,
                        LiteralType: TokenType.U64Literal,
                        Location: _synthLoc) { ResolvedType = u64Type },
                    Location: _synthLoc),
                ElseStatement: body,
                Location: _synthLoc);
        }

        return body;
    }

    /// <summary>
    /// Builds the <c>protocol_info()</c> entity-list body: one <c>ProtocolInfo</c> row per implemented
    /// protocol (name, its routine names, is_generated). Returns <c>false</c> if the ProtocolInfo
    /// entity/list types cannot be resolved.
    /// </summary>
    private bool TryBuildProtocolInfoBody(RoutineInfo routine, TypeInfo owner, TypeInfo textType,
        TypeInfo boolType, TypeInfo listTextType)
    {
        if (ResolveEntityListType(elementTypeName: "ProtocolInfo") is not { } piList)
            return false;
        (TypeInfo protocolInfoType, TypeInfo listProtocolInfo) = piList;

        List<TypeInfo> protocols = owner switch
        {
            RecordTypeInfo r => r.ImplementedProtocols,
            EntityTypeInfo e => e.ImplementedProtocols,
            _ => []
        };

        var rows = protocols
            .Select(p => new List<(string, Expression)>
            {
                (Name: "name", MakeTextLit(value: p.Name, textType: textType)),
                (Name: "routine_names",
                    MakeTextListLiteral(
                        values: p is ProtocolTypeInfo pt
                            ? pt.MemberRoutines.Select(m => m.Name)
                            : System.Linq.Enumerable.Empty<string>(),
                        textType: textType, listTextType: listTextType)),
                (Name: "is_generated", MakeBoolLit(value: false, boolType: boolType))
            })
            .ToList();

        ctx.VariantBodies[key: routine.RegistryKey] = MakeEntityInfoListReturn(
            entityTypeName: "ProtocolInfo", entityType: protocolInfoType,
            listEntityType: listProtocolInfo, rows: rows);
        return true;
    }

    /// <summary>
    /// Builds the <c>routine_info()</c> entity-list body: one <c>RoutineInfo</c> row per member routine
    /// of the owner (name, param types/names, return type, crashable/generated flags, visibility).
    /// Returns <c>false</c> if the RoutineInfo entity/list types cannot be resolved.
    /// </summary>
    private bool TryBuildRoutineInfoBody(RoutineInfo routine, TypeInfo owner, TypeInfo textType,
        TypeInfo boolType, TypeInfo listTextType)
    {
        if (ResolveEntityListType(elementTypeName: "RoutineInfo") is not { } riList)
            return false;
        (TypeInfo routineInfoType, TypeInfo listRoutineInfo) = riList;

        var rows = ctx.Registry.GetMemberRoutinesForType(type: owner)
            .Select(r => new List<(string, Expression)>
            {
                (Name: "name", MakeTextLit(value: r.Name, textType: textType)),
                (Name: "param_types",
                    MakeTextListLiteral(values: r.Parameters.Select(p => p.Type.ShortTypeName),
                        textType: textType, listTextType: listTextType)),
                (Name: "param_names",
                    MakeTextListLiteral(values: r.Parameters.Select(p => p.Name),
                        textType: textType, listTextType: listTextType)),
                (Name: "return_type",
                    MakeTextLit(value: r.ReturnType?.ShortTypeName ?? "None", textType: textType)),
                (Name: "is_crashable", MakeBoolLit(value: r.IsFailable, boolType: boolType)),
                (Name: "is_generated", MakeBoolLit(value: r.IsSynthesized, boolType: boolType)),
                (Name: "visibility", MakeVisibilityLiteral(visibility: r.Visibility))
            })
            .ToList();

        ctx.VariantBodies[key: routine.RegistryKey] = MakeEntityInfoListReturn(
            entityTypeName: "RoutineInfo", entityType: routineInfoType,
            listEntityType: listRoutineInfo, rows: rows);
        return true;
    }

    /// <summary>
    /// Builds the <c>member_variable_info()</c> entity-list body: one <c>FieldInfo</c> row per field
    /// (name, type name, visibility, best-effort C-ABI byte offset). Returns <c>false</c> if U64 or the
    /// FieldInfo entity/list types cannot be resolved.
    /// </summary>
    private bool TryBuildMemberVariableInfoBody(RoutineInfo routine, TypeInfo owner, TypeInfo textType,
        TypeInfo? u64Type)
    {
        if (u64Type == null) return false;
        if (ResolveEntityListType(elementTypeName: "FieldInfo") is not { } fieldList)
            return false;
        (TypeInfo fieldInfoType, TypeInfo listFieldInfo) = fieldList;

        List<MemberVariableInfo> fields = owner switch
        {
            RecordTypeInfo r => r.MemberVariables,
            EntityTypeInfo e => e.MemberVariables,
            _ => []
        };

        ulong[] offsets = ComputeBestEffortFieldOffsets(owner: owner, fields: fields);

        var rows = new List<List<(string, Expression)>>(capacity: fields.Count);
        for (int i = 0; i < fields.Count; i++)
        {
            MemberVariableInfo f = fields[index: i];
            rows.Add(item:
            [
                (Name: "name", MakeTextLit(value: f.Name, textType: textType)),
                (Name: "type_name", MakeTextLit(value: f.Type.ShortTypeName, textType: textType)),
                (Name: "visibility", MakeVisibilityLiteral(visibility: f.Visibility)),
                (Name: "offset", MakeU64Lit(value: offsets[i], u64Type: u64Type))
            ]);
        }

        ctx.VariantBodies[key: routine.RegistryKey] = MakeEntityInfoListReturn(
            entityTypeName: "FieldInfo", entityType: fieldInfoType,
            listEntityType: listFieldInfo, rows: rows);
        return true;
    }

    /// <summary>
    /// Computes cumulative C-ABI byte offsets: align the running cursor to each field's alignment
    /// before placing it, then advance by its size — the layout codegen emits. Layout is not always
    /// computable at synth time (a generic-def owner's field is an unsized parameter T; some backend
    /// types throw in the size walk), so offsets are BEST-EFFORT: any failure zeroes the remaining
    /// offsets rather than aborting the whole build (offset is documented as "byte offset when
    /// available"). @layout("packed") owner: no inter-field padding, so fields sit at the running cursor.
    /// </summary>
    private static ulong[] ComputeBestEffortFieldOffsets(TypeInfo owner, List<MemberVariableInfo> fields)
    {
        bool ownerPacked = owner is RecordTypeInfo { IsPacked: true };
        var offsets = new ulong[fields.Count];
        ulong cursor = 0;
        for (int i = 0; i < fields.Count; i++)
        {
            try
            {
                var align = ownerPacked
                    ? 1ul
                    : (ulong)System.Math.Max(val1: 1,
                        val2: fields[index: i].Type.Alignment(pointerSize: 8));
                cursor = (cursor + align - 1) / align * align;
                offsets[i] = cursor;
                cursor += (ulong)System.Math.Max(val1: 0,
                    val2: fields[index: i].Type.SizeBytes(pointerSize: 8));
            }
            catch
            {
                offsets[i] = 0;
            }
        }
        return offsets;
    }

    /// <summary>
    /// Handles standalone (non-owner) BuilderQuery constants derived from
    /// <see cref="DesugaringContext.Target"/> / <see cref="DesugaringContext.BuildMode"/>.
    /// </summary>
    private bool TryHandleStandaloneBuilderQueryConstant(RoutineInfo routine, TypeInfo textType,
        TypeInfo? u64Type, TypeInfo? byteSizeType)
    {
        switch (routine.Name)
        {
            case "page_size":
                return EmitByteSizeOrU64(routine: routine,
                    value: (ulong)ctx.Target.PageSize,
                    u64Type: u64Type,
                    byteSizeType: byteSizeType);

            case "cache_line":
                return EmitByteSizeOrU64(routine: routine,
                    value: (ulong)ctx.Target.CacheLineSize,
                    u64Type: u64Type,
                    byteSizeType: byteSizeType);

            case "word_size":
                return EmitByteSizeOrU64(routine: routine,
                    value: (ulong)(ctx.Target.PointerBitWidth / 8),
                    u64Type: u64Type,
                    byteSizeType: byteSizeType);

            case "target_os":
                ctx.VariantBodies[key: routine.RegistryKey] =
                    MakeLiteralReturn(value: ctx.Target.TargetOS, returnType: textType);
                return true;

            case "target_arch":
                ctx.VariantBodies[key: routine.RegistryKey] =
                    MakeLiteralReturn(value: ctx.Target.TargetArch, returnType: textType);
                return true;

            case "builder_version":
            {
                // Language-specific: a Suflae compile reports the Suflae version line, a RazorForge compile
                // the RazorForge one — both sourced from the csproj PropertyGroup via AssemblyMetadata
                // (Declaration.BuildInfo), the single source of truth. Folded to a build-time literal.
                string version = ctx.Registry.Language == TypeModel.Enums.Language.Suflae
                    ? Declaration.BuildInfo.SuflaeVersion
                    : Declaration.BuildInfo.RazorForgeVersion;
                ctx.VariantBodies[key: routine.RegistryKey] =
                    MakeLiteralReturn(value: version, returnType: textType);
                return true;
            }

            case "build_timestamp":
                ctx.VariantBodies[key: routine.RegistryKey] =
                    MakeLiteralReturn(value: DateTime.UtcNow.ToString(format: "o"),
                        returnType: textType);
                return true;

            case "build_mode":
            {
                if (routine.ReturnType is ChoiceTypeInfo buildModeChoice)
                {
                    string caseName = ctx.BuildMode switch
                    {
                        RfBuildMode.Debug => "DEBUG",
                        RfBuildMode.Release => "RELEASE",
                        RfBuildMode.ReleaseTime => "RELEASE_TIME",
                        RfBuildMode.ReleaseSpace => "RELEASE_SPACE",
                        _ => throw new InvalidOperationException(
                            $"Unhandled RfBuildMode value '{ctx.BuildMode}'.")
                    };
                    ChoiceCaseInfo? found =
                        buildModeChoice.Cases.FirstOrDefault(c => c.Name == caseName);
                    if (found == null) return false;
                    // Choice discriminants are S32 — emit an S32 literal (ComputedValue is int), not the
                    // S64 the `long` overload of MakeLiteralReturn would produce.
                    ctx.VariantBodies[key: routine.RegistryKey] = new ReturnStatement(
                        Value: new LiteralExpression(Value: found.ComputedValue,
                            LiteralType: TokenType.S32Literal, Location: _synthLoc)
                        { ResolvedType = buildModeChoice },
                        Location: _synthLoc);
                    return true;
                }

                return false;
            }

            default:
                return false;
        }
    }

    private bool EmitByteSizeOrU64(RoutineInfo routine, ulong value, TypeInfo? u64Type,
        TypeInfo? byteSizeType)
    {
        if (byteSizeType == null || u64Type == null)
        {
            return false;
        }

        ctx.VariantBodies[key: routine.RegistryKey] = new ReturnStatement(
            Value: BuilderQueryInliningPass.MakeByteSizeCreatorPublic(value: value,
                u64Type: u64Type,
                byteSizeType: byteSizeType,
                loc: _synthLoc),
            Location: _synthLoc);
        return true;
    }

    /// <summary>
    /// Builds a <c>return [elem0, elem1, ...]</c> statement using a
    /// <see cref="ListLiteralExpression"/> with the given Text string values.
    /// </summary>
    private static ReturnStatement MakeListReturn(List<string> values, TypeInfo textType,
        TypeInfo listTextType)
    {
        var elements = values.Select(v => (Expression)MakeTextLit(value: v, textType: textType))
                             .ToList();
        return new ReturnStatement(
            Value: new ListLiteralExpression(Elements: elements,
                ElementType: null,
                Location: _synthLoc) { ResolvedType = listTextType },
            Location: _synthLoc);
    }

    //  BuilderQuery entity-list metadata helpers (member_variable_info / protocol_info / routine_info)

    /// <summary>Builds a <c>Text</c> literal expression carrying <paramref name="value"/>.</summary>
    private static LiteralExpression MakeTextLit(string value, TypeInfo textType) =>
        new(Value: value, LiteralType: TokenType.TextLiteral, Location: _synthLoc)
        { ResolvedType = textType };

    /// <summary>Builds a <c>U64</c> literal expression carrying <paramref name="value"/>.</summary>
    private static LiteralExpression MakeU64Lit(ulong value, TypeInfo u64Type) =>
        new(Value: value, LiteralType: TokenType.U64Literal, Location: _synthLoc)
        { ResolvedType = u64Type };

    /// <summary>Builds a <c>Bool</c> literal expression carrying <paramref name="value"/>.</summary>
    private static LiteralExpression MakeBoolLit(bool value, TypeInfo boolType) =>
        new(Value: value, LiteralType: value ? TokenType.True : TokenType.False, Location: _synthLoc)
        { ResolvedType = boolType };

    /// <summary>Builds an inline <c>List[Text]</c> literal (for nested <c>routine_names</c>/<c>param_*</c> fields).</summary>
    private static ListLiteralExpression MakeTextListLiteral(IEnumerable<string> values,
        TypeInfo textType, TypeInfo listTextType) =>
        new(Elements: values.Select(v => (Expression)MakeTextLit(value: v, textType: textType)).ToList(),
            ElementType: null, Location: _synthLoc) { ResolvedType = listTextType };

    /// <summary>
    /// Maps a compiler <see cref="VisibilityModifier"/> to the corresponding <c>Visibility</c> choice
    /// case, emitted as the BARE case-name identifier (<c>OPEN</c>/<c>POSTED</c>/<c>SECRET</c>) exactly
    /// as source would write it — <see cref="AnalyzeCompilerGeneratedBody"/> re-analyzes variant bodies,
    /// resolving the case via <c>LookupChoiceCase</c> (a preset S32/S64 literal typed as the choice would
    /// be re-derived to its backing int and mismatch the <c>Visibility</c> field, silently breaking the
    /// body). The case names are globally unique, so the bare form resolves unambiguously.
    /// </summary>
    private static Expression MakeVisibilityLiteral(VisibilityModifier visibility)
    {
        string caseName = visibility switch
        {
            VisibilityModifier.Open => "OPEN",
            VisibilityModifier.Posted => "POSTED",
            VisibilityModifier.Secret => "SECRET",
            _ => "OPEN"
        };
        return new IdentifierExpression(Name: caseName, Location: _synthLoc);
    }

    /// <summary>
    /// Builds a <c>return [E(...), E(...), …]</c> body for a BuilderQuery entity-list metadata routine:
    /// each row becomes an <c>E(field: value, …)</c> creator, collected into a <c>List[Owned[E]]</c>
    /// literal (the surface <c>List[E]</c> for an entity <c>E</c>). Element-wrap-in-Owned and the
    /// create()+add_last lowering are handled downstream exactly as for a user-written list literal.
    /// </summary>
    private static ReturnStatement MakeEntityInfoListReturn(string entityTypeName,
        TypeInfo entityType, TypeInfo listEntityType,
        List<List<(string Name, Expression Value)>> rows)
    {
        var elements = rows
            .Select(row => (Expression)new CreatorExpression(TypeName: entityTypeName,
                TypeArguments: null, MemberVariables: row, Location: _synthLoc)
            { ResolvedType = entityType })
            .ToList();
        return new ReturnStatement(
            Value: new ListLiteralExpression(Elements: elements, ElementType: null,
                Location: _synthLoc) { ResolvedType = listEntityType },
            Location: _synthLoc);
    }

    /// <summary>
    /// Resolves <c>List[<paramref name="elementTypeName"/>]</c> for an entity element type, returning
    /// <c>null</c> if either def is unavailable. Guarded by the entity-list metadata routines. A list of
    /// entities is typed <c>List[E]</c> directly (no <c>Owned</c> wrapper appears in the surface type —
    /// see the desugared form of a source <c>List[FieldInfo]</c> literal).
    /// </summary>
    private (TypeInfo entityType, TypeInfo listEntity)? ResolveEntityListType(string elementTypeName)
    {
        // FieldInfo/ProtocolInfo/RoutineInfo live in `module BuilderQuery` — qualify (a bare lookup
        // depended on the cross-module short-name scan; scan-off it missed, the synthesized
        // member_variable_info/protocol_info/routine_info body bailed, and its routine over-pruned).
        TypeInfo? entityType = ctx.Registry.LookupType(name: $"BuilderQuery.{elementTypeName}")
                               ?? ctx.Registry.LookupType(name: elementTypeName);
        TypeInfo? listDef = ctx.Registry.LookupType(name: "List");
        if (entityType == null || listDef == null) return null;
        TypeInfo listEntity =
            ctx.Registry.GetOrCreateResolution(genericDef: listDef, typeArguments: [entityType]);
        return (entityType, listEntity);
    }

    /// <summary>
    /// Builds the auto-derived <c>destroy()</c> body. Composite record/entity/crashable types
    /// recurse into their owned fields (<c>me.field.destroy()</c> for each); scalar kinds
    /// (choices, flags, <c>@llvm</c>-backed primitives, tuples, variants) get a no-op return.
    /// Leaf RC/ptr teardown (Hijacked, Retained/Tracked, Viewing/Modifying) lives in hand-written
    /// wrapper destructors and is never reached here (those types keep their own <c>destroy</c>).
    /// </summary>
    /// <summary>True if <paramref name="t"/> is a <c>Roamed[U]</c> field type (a biased-RC handle).</summary>
    private static bool IsRoamedField(TypeInfo? t)
    {
        if (t == null) return false;
        string baseName = t switch
        {
            WrapperTypeInfo w => w.Name,
            RecordTypeInfo { GenericDefinition: { } d } => d.Name,
            _ => t.BareName
        };
        return baseName == Declaration.RuntimeContract.Roamed;
    }

    /// <summary>
    /// Builds the cycle-collector trace hook <c>roam_trace_impl()</c> for an entity: one
    /// <c>me.&lt;field&gt;.cyclic_visit()</c> per <c>Roamed[U]</c> field (reports the field's
    /// controller to the collector). Non-Roamed fields cannot form strong cycles and are skipped;
    /// an entity with no Roamed fields gets an empty (return-only) body.
    /// </summary>
    private Statement BuildRoamTraceBody(TypeInfo? owner)
    {
        var noop = new ReturnStatement(Value: null, Location: _synthLoc);
        List<MemberVariableInfo>? fields = owner is EntityTypeInfo e
            ? e.MemberVariables
            : null;
        if (fields is null or { Count: 0 })
            return noop;

        TypeInfo? noneType = ctx.Registry.LookupType(name: "None");
        var statements = new List<Statement>(capacity: fields.Count + 1);
        foreach (MemberVariableInfo field in fields)
        {
            // Both non-null (`x: E`) and optional (`x: E?`) entity fields are bare `Roamed[E]` in Suflae,
            // so IsRoamedField covers both — the collector traces through a null (none) handle harmlessly.
            if (!IsRoamedField(t: field.Type))
                continue;
            var meRef = new IdentifierExpression(Name: "me", Location: _synthLoc)
            {
                ResolvedType = owner
            };
            var fieldRef =
                new MemberExpression(Object: meRef, MemberName: field.Name, Location: _synthLoc)
                {
                    ResolvedType = field.Type
                };
            var visitCall = new CallExpression(
                Callee: new MemberExpression(Object: fieldRef,
                    MemberName: "cyclic_visit",
                    Location: _synthLoc) { ResolvedType = noneType },
                Arguments: [],
                Location: _synthLoc) { ResolvedType = noneType };
            statements.Add(item: new ExpressionStatement(Expression: visitCall,
                Location: _synthLoc));
        }

        // Nested aggregate: a bare (non-Roamed) ENTITY field owns its own roam machinery — the SF
        // container overlay's `inner: RF::Core.List[T]` is the canonical case, a single-owner RazorForge
        // list whose `Hijacked[T]` buffer holds the `Roamed` elements. The field-walk above treats that
        // bare entity as an opaque leaf, so its held elements are invisible to the collector. Delegate to
        // the field's OWN `roam_trace_impl` so the trace descends into the nested aggregate (whose buffer
        // branch below then visits each element). Infinite recursion is impossible: a bare entity field is
        // single-owner CONTAINMENT, which is acyclic by construction — a cycle needs SHARED ownership, i.e.
        // a `Roamed`, and those are handled by `cyclic_visit` (color-based, cycle-safe) above. Trivially
        // destructible fields cannot transitively hold a `Roamed`, so they are skipped.
        foreach (MemberVariableInfo field in fields)
        {
            if (IsRoamedField(t: field.Type) || field.Type is not EntityTypeInfo)
                continue;
            var fieldRef = new MemberExpression(
                Object: new IdentifierExpression(Name: "me", Location: _synthLoc) { ResolvedType = owner },
                MemberName: field.Name, Location: _synthLoc) { ResolvedType = field.Type };
            var traceImplCall = new CallExpression(
                Callee: new MemberExpression(Object: fieldRef, MemberName: "roam_trace_impl",
                    Location: _synthLoc) { ResolvedType = noneType },
                Arguments: [],
                Location: _synthLoc) { ResolvedType = noneType };
            statements.Add(item: new ExpressionStatement(Expression: traceImplCall,
                Location: _synthLoc));
        }

        // Container buffer: a lone `Hijacked[T]` field paired with a `count: U64` field is a dense
        // element buffer (List). The field-walk above treats the raw pointer as an opaque leaf, so
        // emit `me.<buf>.cyclic_trace_buffer(count: me.count)` to visit each element (a no-op when the
        // element `T` is a value — `cyclic_visit` is a universal `@overridable` no-op). Gated to a
        // SINGLE Hijacked field so a multi-buffer / sparse container (Dict) does not misfire — those
        // carry their own trace.
        List<MemberVariableInfo> buffers =
            fields.Where(predicate: f => IsHijackedField(t: f.Type)).ToList();
        MemberVariableInfo? countField =
            fields.FirstOrDefault(predicate: f => f.Name == "count");
        if (buffers is [{ } buffer] && countField != null)
        {
            var bufRef = new MemberExpression(
                Object: new IdentifierExpression(Name: "me", Location: _synthLoc) { ResolvedType = owner },
                MemberName: buffer.Name, Location: _synthLoc) { ResolvedType = buffer.Type };
            var countRef = new MemberExpression(
                Object: new IdentifierExpression(Name: "me", Location: _synthLoc) { ResolvedType = owner },
                MemberName: "count", Location: _synthLoc) { ResolvedType = countField.Type };
            var traceCall = new CallExpression(
                Callee: new MemberExpression(Object: bufRef, MemberName: "cyclic_trace_buffer",
                    Location: _synthLoc) { ResolvedType = noneType },
                Arguments: [countRef],
                Location: _synthLoc) { ResolvedType = noneType };
            statements.Add(item: new ExpressionStatement(Expression: traceCall, Location: _synthLoc));
        }

        // Sparse container (open-addressing Dict/Set): a per-entry liveness bitmap `entry_live: Hijacked[U8]`
        // plus an `entries_used` high-water mark identify a hash container whose element buffers (`keys`/
        // `vals`/`slots`) are indexed 0..entries_used WITH HOLES. The dense single-buffer branch above does
        // NOT fire (Dict/Set carry several Hijacked fields), so trace each ELEMENT buffer through
        // `cyclic_trace_sparse_buffer(live, used)`, which reports only the live slots (R2). An element buffer
        // is a Hijacked field whose inner type is NOT trivially-destructible — the generic `keys`/`vals`/
        // `slots`, never the scalar `Hijacked[U8]`/`Hijacked[U64]` ctrl/indices/entry_live metadata (which
        // cannot transitively hold a Roamed). Same visibility guarantee as the List buffer: `cyclic_visit`
        // no-ops for value elements, reports each `Roamed` handle.
        MemberVariableInfo? entryLiveField =
            fields.FirstOrDefault(predicate: f => f.Name == "entry_live" && IsHijackedField(t: f.Type));
        MemberVariableInfo? entriesUsedField =
            fields.FirstOrDefault(predicate: f => f.Name == "entries_used");
        if (entryLiveField != null && entriesUsedField != null)
        {
            foreach (MemberVariableInfo field in fields)
            {
                if (!IsHijackedField(t: field.Type) || ReferenceEquals(objA: field, objB: entryLiveField))
                    continue;
                TypeInfo? inner = HijackedInnerType(t: field.Type);
                if (inner == null)
                    continue;
                MemberExpression MeField(string name, TypeInfo? type) => new(
                    Object: new IdentifierExpression(Name: "me", Location: _synthLoc) { ResolvedType = owner },
                    MemberName: name, Location: _synthLoc) { ResolvedType = type };
                var sparseCall = new CallExpression(
                    Callee: new MemberExpression(Object: MeField(field.Name, field.Type),
                        MemberName: "cyclic_trace_sparse_buffer", Location: _synthLoc) { ResolvedType = noneType },
                    Arguments:
                    [
                        new NamedArgumentExpression(Name: "live",
                            Value: MeField("entry_live", entryLiveField.Type), Location: _synthLoc),
                        new NamedArgumentExpression(Name: "used",
                            Value: MeField("entries_used", entriesUsedField.Type), Location: _synthLoc)
                    ],
                    Location: _synthLoc) { ResolvedType = noneType };
                statements.Add(item: new ExpressionStatement(Expression: sparseCall, Location: _synthLoc));
            }
        }

        statements.Add(item: noop);
        return new BlockStatement(Statements: statements, Location: _synthLoc);
    }

    /// <summary>True if <paramref name="t"/> is a <c>Hijacked[U]</c> raw-buffer field type.</summary>
    private static bool IsHijackedField(TypeInfo? t)
    {
        if (t == null) return false;
        string baseName = t switch
        {
            WrapperTypeInfo w => w.Name,
            RecordTypeInfo { GenericDefinition: { } d } => d.Name,
            _ => t.BareName
        };
        return baseName == Declaration.RuntimeContract.Hijacked;
    }

    /// <summary>The element type <c>U</c> of a <c>Hijacked[U]</c> field type, or null. Used by the
    /// sparse-container roam-trace to tell an element buffer (generic <c>keys</c>/<c>vals</c>/<c>slots</c>)
    /// from a scalar metadata buffer (<c>Hijacked[U8]</c>/<c>Hijacked[U64]</c> ctrl/indices/entry_live).</summary>
    private static TypeInfo? HijackedInnerType(TypeInfo? t) => t switch
    {
        WrapperTypeInfo w => w.InnerType,
        _ => t?.TypeArguments is { Count: > 0 } args ? args[index: 0] : null
    };

    /// <summary>
    /// Builds the cycle-collector free hook <c>roam_free_impl()</c> for an entity: tears down each
    /// NON-Roamed field (its own resources) then frees the entity allocation. Roamed fields are
    /// deliberately NOT torn down — the collector frees the whole white cycle directly, so recursing
    /// through a Roamed child's <c>destroy</c> here would double-free a sibling being reaped in the
    /// same batch (the finalizer-recursion hazard).
    /// </summary>
    private Statement BuildRoamFreeBody(TypeInfo? owner)
    {
        var noop = new ReturnStatement(Value: null, Location: _synthLoc);
        List<MemberVariableInfo>? fields = owner is EntityTypeInfo e
            ? e.MemberVariables
            : null;

        TypeInfo? noneType = ctx.Registry.LookupType(name: "None");
        var statements = new List<Statement>(capacity: (fields?.Count ?? 0) + 2);
        if (fields is { Count: > 0 })
        {
            foreach (MemberVariableInfo field in fields)
            {
                if (IsRoamedField(t: field.Type))
                    continue;
                var meRef = new IdentifierExpression(Name: "me", Location: _synthLoc)
                {
                    ResolvedType = owner
                };
                var fieldRef =
                    new MemberExpression(Object: meRef,
                        MemberName: field.Name,
                        Location: _synthLoc) { ResolvedType = field.Type };
                var destroyCall = new CallExpression(
                    Callee: new MemberExpression(Object: fieldRef,
                        MemberName: "destroy",
                        Location: _synthLoc) { ResolvedType = noneType },
                    Arguments: [],
                    Location: _synthLoc) { ResolvedType = noneType };
                statements.Add(item: new ExpressionStatement(Expression: destroyCall,
                    Location: _synthLoc));
            }
        }

        if (owner is EntityTypeInfo)
            statements.Add(item: BuildEntitySelfFree(owner: owner!, noneType: noneType));

        statements.Add(item: noop);
        return new BlockStatement(Statements: statements, Location: _synthLoc);
    }


    /// <summary>
    /// Builds <c>me.hijack().invalidate()</c> — frees the heap allocation backing an entity.
    /// Mirrors the tail of hand-written entity destructors (e.g. <c>List[T].destroy</c>); the
    /// synthesized destructor must emit it too, or every auto-derived entity leaks its struct.
    /// </summary>
    private ExpressionStatement BuildEntitySelfFree(TypeInfo owner, TypeInfo? noneType)
    {
        TypeInfo hijackedType = ctx.Registry.GetOrCreateWrapperType(
            wrapperName: Declaration.RuntimeContract.Hijacked,
            innerType: owner,
            isReadOnly: false);

        var meRef = new IdentifierExpression(Name: "me", Location: _synthLoc)
        {
            ResolvedType = owner
        };
        var hijackCall = new CallExpression(
            Callee: new MemberExpression(Object: meRef,
                MemberName: Declaration.RuntimeContract.RawPointer.Hijack,
                Location: _synthLoc) { ResolvedType = hijackedType },
            Arguments: [],
            Location: _synthLoc) { ResolvedType = hijackedType };
        var invalidateCall = new CallExpression(
            Callee: new MemberExpression(Object: hijackCall,
                MemberName: Declaration.RuntimeContract.RawPointer.Invalidate,
                Location: _synthLoc) { ResolvedType = noneType },
            Arguments: [],
            Location: _synthLoc) { ResolvedType = noneType };
        return new ExpressionStatement(Expression: invalidateCall, Location: _synthLoc);
    }

    private static ReturnStatement MakeLiteralReturn(string value, TypeInfo returnType) =>
        new ReturnStatement(
            Value: new LiteralExpression(Value: value,
                LiteralType: TokenType.TextLiteral,
                Location: _synthLoc) { ResolvedType = returnType },
            Location: _synthLoc);

    private static ReturnStatement MakeLiteralReturn(ulong value, TypeInfo returnType) =>
        new ReturnStatement(
            Value: new LiteralExpression(Value: value,
                LiteralType: TokenType.U64Literal,
                Location: _synthLoc) { ResolvedType = returnType },
            Location: _synthLoc);

    private static ReturnStatement MakeLiteralReturn(long value, TypeInfo returnType) =>
        new ReturnStatement(
            Value: new LiteralExpression(Value: value,
                LiteralType: TokenType.S64Literal,
                Location: _synthLoc) { ResolvedType = returnType },
            Location: _synthLoc);

    private static ReturnStatement MakeLiteralReturn(bool value, TypeInfo returnType) =>
        new ReturnStatement(Value: new LiteralExpression(Value: value,
                LiteralType: value
                    ? TokenType.True
                    : TokenType.False,
                Location: _synthLoc) { ResolvedType = returnType },
            Location: _synthLoc);

    //  represent / diagnose (tuple)

    private void HandleTuple(RoutineInfo routine, TupleTypeInfo tuple, TypeInfo textType,
        TypeInfo? s32Type)
    {
        switch (routine.Name)
        {
            case "destroy" when routine.Parameters.Count == 0:
                // Tuples are filtered out of the main routine loop (they never appear in routine
                // signatures), so the unified `destroy` synthesis never sees them. Clone the universal
                // `T.destroy()` derive (the `expand allmemvarof` field-walk) here so owned elements
                // (e.g. a `Text`) are torn down — otherwise the ScopeTeardownLoweringPass call is undefined.
                ctx.VariantBodies[key: routine.RegistryKey] =
                    CloneUniversalDeriveBody(ownerType: tuple, synthesized: routine,
                        memberRoutineName: "destroy")
                    ?? throw new System.InvalidOperationException(
                        message: $"destroy derive could not be cloned for tuple '{tuple.FullName}'.");
                break;

            // Tuples have a SPECIAL text format — represent `(1, 2)` (no type name / field names),
            // diagnose `Tuple[…](1, 2)` — produced by the `@override … needs T is TupleType` derive
            // template (NOT the universal `TypeName(field: value, …)` shape); falls back to the C# builder.
            case RepresentMemberRoutineName:
                ctx.VariantBodies[key: routine.RegistryKey] =
                    CloneUniversalDeriveBody(ownerType: tuple, synthesized: routine,
                        memberRoutineName: RepresentMemberRoutineName)
                    ?? BuildTupleTextBody(tuple: tuple, textType: textType, diagnose: false);
                break;

            case DiagnoseMemberRoutineName:
                ctx.VariantBodies[key: routine.RegistryKey] =
                    CloneUniversalDeriveBody(ownerType: tuple, synthesized: routine,
                        memberRoutineName: DiagnoseMemberRoutineName)
                    ?? BuildTupleTextBody(tuple: tuple, textType: textType, diagnose: true);
                break;

            case SerializeMemberRoutineName:
            {
                // A generic tuple (`Tuple[U64, T]`) has no concrete body — its serialize is cloned per
                // CONCRETE instantiation during monomorphization; synthesizing one here would send the
                // template's `SerialValue(…)` constructor to codegen with the unresolved `T` (RF-S959).
                if (tuple.ElementTypes.Any(predicate: e => e is GenericParameterTypeInfo)) break;
                // Field-walk item0/item1/… into a `Dict[Text, SerialValue]` via the SAME universal
                // serialize derive template as records/entities (routine elements box their
                // `represent` signature via the template's `m.is_routine` branch). Registered in
                // GetOrCreateTupleType only when every element is serializable-or-routine.
                Statement? tupSer =
                    CloneUniversalDeriveBody(ownerType: tuple, synthesized: routine,
                        memberRoutineName: SerializeMemberRoutineName)
                    ?? BuildSerializeBody(owner: tuple, fields: tuple.MemberVariables,
                        textType: textType);
                if (tupSer != null) ctx.VariantBodies[key: routine.RegistryKey] = tupSer;
                break;
            }

            // A tuple is a RecordTypeInfo, so its retaining field-walk copy reuses the record
            // builder: reconstruct `(me.item0.assign(), me.item1, …)` — each retaining element goes
            // through its own `store`, value elements stay shallow. The Creator it emits is exactly
            // what ExpressionLoweringPass lowers a tuple literal to (TypeName = tuple.Name, item{i}
            // fields), so codegen materializes it identically.
            case "assign" when routine.Parameters.Count == 0:
                ctx.VariantBodies[key: routine.RegistryKey] = BuildRecordCopyBody(record: tuple);
                break;

            case "eq":
            {
                TypeInfo? boolType = ctx.Registry.LookupType(name: "Bool");
                if (boolType == null) break;
                ctx.VariantBodies[key: routine.RegistryKey] =
                    CloneUniversalDeriveBody(ownerType: tuple, synthesized: routine,
                        memberRoutineName: "eq")
                    ?? BuildEqBody(ownerType: tuple, fields: tuple.MemberVariables,
                        boolType: boolType);
                break;
            }

            case "cmp":
            {
                TypeInfo? boolType = ctx.Registry.LookupType(name: "Bool");
                if (s32Type == null || boolType == null) break;
                ctx.VariantBodies[key: routine.RegistryKey] =
                    CloneUniversalDeriveBody(ownerType: tuple, synthesized: routine,
                        memberRoutineName: "cmp")
                    ?? BuildCmpBody(ownerType: tuple, fields: tuple.MemberVariables,
                        s32Type: s32Type, boolType: boolType);
                break;
            }

            case HashMemberRoutineName:
            {
                TypeInfo? u64Type = ctx.Registry.LookupType(name: "U64");
                if (u64Type == null) break;
                // The 0-param `hash()` clones the universal derive template (comptime `expand`
                // XOR-fold); the keyed `hash(k0, k1)` keeps the C# builder unchanged.
                ctx.VariantBodies[key: routine.RegistryKey] =
                    (routine.Parameters.Count == 0
                        ? CloneUniversalDeriveBody(ownerType: tuple, synthesized: routine,
                            memberRoutineName: HashMemberRoutineName)
                        : null)
                    ?? BuildHashBody(ownerType: tuple, fields: tuple.MemberVariables,
                        u64Type: u64Type);
                break;
            }
        }
    }

    /// <summary>
    /// Builds the body for <c>represent</c> or <c>diagnose</c> on a tuple.
    /// <list type="bullet">
    ///   <item><c>represent</c>: <c>return f"({me.item0}, {me.item1})"</c></item>
    ///   <item><c>diagnose</c>: <c>return f"ValueTuple[T1, T2]({me.item0}, {me.item1})"</c></item>
    /// </list>
    /// </summary>
    private static ReturnStatement BuildTupleTextBody(TupleTypeInfo tuple, TypeInfo textType,
        bool diagnose)
    {
        var parts = new List<InsertedTextPart>();

        if (diagnose)
        {
            parts.Add(new TextPart(Text: $"{tuple.QualifiedTypeName}(", Location: _synthLoc));
        }
        else
        {
            parts.Add(new TextPart(Text: "(", Location: _synthLoc));
        }

        bool first = true;
        foreach (MemberVariableInfo field in tuple.MemberVariables)
        {
            if (!first)
                parts.Add(new TextPart(Text: ", ", Location: _synthLoc));
            first = false;

            var fieldExpr = new MemberExpression(
                Object: new IdentifierExpression(Name: "me", Location: _synthLoc)
                {
                    ResolvedType = tuple
                },
                MemberName: field.Name,
                Location: _synthLoc) { ResolvedType = field.Type };

            parts.Add(new ExpressionPart(Expression: fieldExpr,
                FormatSpec: null,
                Location: _synthLoc));
        }

        parts.Add(new TextPart(Text: ")", Location: _synthLoc));

        var fstring =
            new InsertedTextExpression(Parts: parts, IsRaw: false, Location: _synthLoc)
            {
                ResolvedType = textType
            };

        return new ReturnStatement(Value: fstring, Location: _synthLoc);
    }

    //  represent / diagnose (variant)

    private void HandleVariant(RoutineInfo routine, VariantTypeInfo variant, TypeInfo textType)
    {
        // Skip generic definitions -> no concrete member types to dispatch on.
        if (variant.IsGenericDefinition) return;

        // Synthesize the per-arm EXTRACTORS (`Arm.create!(from: V)`) here. They are owned by the arm
        // type; for a GENERIC-instance arm (e.g. `Dict[Text, SerialValue]`) the main synthesis loop's
        // `GetAllRoutines` liveness filter (IsConcreteTypeLive) excludes the arm-owned routine at this
        // phase, so its body would never be built. We hold the arm list here, so build them directly
        // (idempotent — guarded by ContainsKey). The BOX direction is owned by the concrete variant and
        // is synthesized by the main-loop hook as usual.
        SynthesizeVariantArmExtractors(variant: variant);

        switch (routine.Name)
        {
            case RepresentMemberRoutineName:
                // The `@override needs T is variant` derive template (arm-dispatch via `branchof`) is
                // selected for a variant; falls back to the C# builder if absent.
                ctx.VariantBodies[key: routine.RegistryKey] =
                    CloneUniversalDeriveBody(ownerType: variant, synthesized: routine,
                        memberRoutineName: RepresentMemberRoutineName)
                    ?? BuildVariantRepresentBody(variant: variant, textType: textType);
                break;

            case DiagnoseMemberRoutineName:
                // TAG-dispatch from the `@override … needs T is VariantType` derive template
                // (`branchof` + `m.type_id` + `v.diagnose()`); falls back to the C# builder.
                ctx.VariantBodies[key: routine.RegistryKey] =
                    CloneUniversalDeriveBody(ownerType: variant, synthesized: routine,
                        memberRoutineName: DiagnoseMemberRoutineName)
                    ?? BuildVariantDiagnoseBody(variant: variant, textType: textType);
                break;

            case "duplicate":
                // TAG-dispatch deep copy from the `@override … needs T is VariantType` derive
                // template (arm reconstruction `is ${m.type} v => Me(from: v.copy())`); C# fallback.
                ctx.VariantBodies[key: routine.RegistryKey] =
                    CloneUniversalDeriveBody(ownerType: variant, synthesized: routine,
                        memberRoutineName: "duplicate")
                    ?? BuildVariantCopyBody(variant: variant);
                break;
        }
    }

    /// <summary>
    /// Builds a variant's deep <c>copy</c>:
    /// <c>when me { is HeapArm as v => return Variant.HeapArm(v.copy()), … else => return me }</c>.
    /// Each arm whose payload owns a real destructor (a collection, a managed leaf like <c>Text</c>, a
    /// record that transitively owns one) is reconstructed with an independent <c>arm.copy()</c> so the
    /// result shares no heap storage — a bitwise alias would double-free when both owners tear down.
    /// Scalar / <c>None</c> / <c>None</c> arms are safe to bitwise-duplicate, so they fall to the
    /// <c>else => return me</c> identity branch (RecordCopyLoweringPass treats this copy body like
    /// <c>store</c>, so the bare <c>return me</c> is not re-injected).
    /// </summary>
    private WhenStatement BuildVariantCopyBody(VariantTypeInfo variant)
    {
        var meRef = new IdentifierExpression(Name: "me", Location: _synthLoc)
        {
            ResolvedType = variant
        };

        var clauses = new List<WhenClause>(capacity: variant.Members.Count + 1);
        foreach (VariantMemberInfo member in variant.Members)
        {
            if (member.IsNone || member.Type is null)
                continue;

            Declaration.TypeRegistry.Lifecycle armLc =
                ctx.Registry.GetLifecycle(type: member.Type);
            if (armLc.IsBorrow)
                continue; // borrow-tier arm — cannot copy; the else branch bitwise-forwards it.
            // NOTE: do NOT skip on `armLc.Destroy is null`. A generic ENTITY-instance arm
            // (Dict[Text, SerialValue], List[SerialValue]) reports a null destructor here because
            // its instance memberRoutines aren't materialized at Phase-6 synthesis time — yet it is a heap
            // reference that DOUBLE-FREES if bitwise-aliased. Emit `arm.copy()` for every non-borrow
            // arm (identity for scalars, deep for heap arms); only None arms fall to the else branch.

            var typeExpr =
                new TypeExpression(Name: member.Type.Name,
                    GenericArguments: null,
                    Location: _synthLoc) { ResolvedType = member.Type };
            var pattern = new TypePattern(Type: typeExpr,
                VariableName: "v",
                Bindings: null,
                Location: _synthLoc);

            var vRef = new IdentifierExpression(Name: "v", Location: _synthLoc)
            {
                ResolvedType = member.Type
            };
            var copyCall = new CallExpression(
                Callee: new MemberExpression(Object: vRef, MemberName: "duplicate", Location: _synthLoc)
                {
                    ResolvedType = member.Type
                },
                Arguments: [],
                Location: _synthLoc) { ResolvedType = member.Type };

            var boxed =
                new CreatorExpression(TypeName: variant.Name,
                    TypeArguments: null,
                    MemberVariables: [(member.Type.Name, copyCall)],
                    Location: _synthLoc) { ResolvedType = variant, ConstructedType = variant };

            clauses.Add(item: new WhenClause(Pattern: pattern,
                Body: new ReturnStatement(Value: boxed, Location: _synthLoc),
                Location: _synthLoc));
        }

        clauses.Add(item: new WhenClause(
            Pattern: new ElsePattern(VariableName: null, Location: _synthLoc),
            Body: new ReturnStatement(Value: meRef, Location: _synthLoc),
            Location: _synthLoc));

        return new WhenStatement(Expression: meRef, Clauses: clauses, Location: _synthLoc);
    }

    /// <summary>
    /// Synthesizes the body of an auto-generated variant arm constructor, if <paramref name="routine"/>
    /// is one. Two shapes (both keyed off the arm/variant relationship, not the routine name text):
    /// <list type="bullet">
    ///   <item><c>V.create(from: Arm) -> V</c> — box: <c>return &lt;V with the Arm-tagged payload&gt;</c>.</item>
    ///   <item><c>Arm.create!(from: V) -> Arm</c> — failable extract:
    ///     <c>when from { is Arm as v => return v, else => absent }</c>.</item>
    /// </list>
    /// Only synthesized (auto) constructors are handled; a hand-written <c>create</c> keeps its body.
    /// </summary>
    private bool TryBuildVariantArmConstructorBody(RoutineInfo routine, out Statement? body)
    {
        body = null;
        if (!routine.IsSynthesized || routine.Kind != RoutineKind.Creator ||
            routine.Parameters is not [{ Name: "from" } fromParam] || fromParam.Type is null)
        {
            return false;
        }

        // Box: owner is the variant, `from` is one of its arms.
        if (routine.Name == "create" && routine.OwnerType is VariantTypeInfo boxVariant &&
            FindArmByType(variant: boxVariant, armType: fromParam.Type) is
                { Type: { } boxArmType })
        {
            var fromRef = new IdentifierExpression(Name: "from", Location: _synthLoc)
            {
                ResolvedType = fromParam.Type
            };
            var boxed =
                new CreatorExpression(TypeName: boxVariant.Name,
                    TypeArguments: null,
                    MemberVariables: [(boxArmType.Name, fromRef)],
                    Location: _synthLoc)
                {
                    ResolvedType = boxVariant, ConstructedType = boxVariant
                };
            body = new ReturnStatement(Value: boxed, Location: _synthLoc);
            return true;
        }

        // Failable extract: `from` is a variant, owner is one of its arms.
        if (routine.Name == "create" && routine.IsFailable &&
            fromParam.Type is VariantTypeInfo fromVariant && routine.OwnerType is { } armOwner &&
            FindArmByType(variant: fromVariant, armType: armOwner) is { Type: { } })
        {
            var fromRef = new IdentifierExpression(Name: "from", Location: _synthLoc)
            {
                ResolvedType = fromVariant
            };
            var typeExpr =
                new TypeExpression(Name: armOwner.Name,
                    GenericArguments: null,
                    Location: _synthLoc) { ResolvedType = armOwner };
            var vRef = new IdentifierExpression(Name: "v", Location: _synthLoc)
            {
                ResolvedType = armOwner
            };
            // The pattern binding `v` is a VIEW into `from`'s payload. Returning it bare hands the
            // caller an alias to the variant's heap payload; when the caller owns the result AND the
            // source variant tears its payload down, the same heap is freed twice. Deep-copy the
            // payload out so the extracted value is independent. `copy` is AlwaysLive for every type
            // — identity for scalars, deep for heap arms (Dict/List/Text). We can't gate on
            // GetLifecycle here because a generic-instance arm (Dict[..]/List[..]) reports a null
            // destructor at synth time (not-yet-live), which is exactly the arm that MUST be copied.
            Expression extracted = new CallExpression(
                Callee: new MemberExpression(Object: vRef, MemberName: "duplicate", Location: _synthLoc)
                {
                    ResolvedType = armOwner
                },
                Arguments: [],
                Location: _synthLoc) { ResolvedType = armOwner };
            var matchClause = new WhenClause(
                Pattern: new TypePattern(Type: typeExpr,
                    VariableName: "v",
                    Bindings: null,
                    Location: _synthLoc),
                Body: new ReturnStatement(Value: extracted, Location: _synthLoc),
                Location: _synthLoc);
            var elseClause = new WhenClause(
                Pattern: new ElsePattern(VariableName: null, Location: _synthLoc),
                Body: new AbsentStatement(Location: _synthLoc),
                Location: _synthLoc);
            body = new WhenStatement(Expression: fromRef,
                Clauses: [matchClause, elseClause],
                Location: _synthLoc);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Builds the failable extractor body <c>Arm.create!(from: V)</c> for each non-None arm and stores
    /// it under that routine's key, so a GENERIC-instance arm (excluded from the liveness-gated main
    /// synthesis loop) still gets a body. Idempotent — skips arms whose extractor body already exists.
    /// </summary>
    private void SynthesizeVariantArmExtractors(VariantTypeInfo variant)
    {
        foreach (VariantMemberInfo arm in variant.Members)
        {
            if (arm.IsNone || arm.Type is null)
            {
                continue;
            }

            RoutineInfo? extractor = ctx.Registry
                                        .GetMemberRoutinesForType(type: arm.Type)
                                        .FirstOrDefault(predicate: m =>
                                             m is { Name: "create", IsFailable: true } &&
                                             m.Parameters is [{ Type: { } paramType }] &&
                                             paramType.FullName == variant.FullName);
            if (extractor is null || ctx.VariantBodies.ContainsKey(key: extractor.RegistryKey))
            {
                continue;
            }

            if (TryBuildVariantArmConstructorBody(routine: extractor,
                    body: out Statement? exBody) && exBody is not null)
            {
                ctx.VariantBodies[key: extractor.RegistryKey] = exBody;
            }
        }
    }

    /// <summary>Finds the variant arm whose payload type matches <paramref name="armType"/> by full name.</summary>
    private static VariantMemberInfo? FindArmByType(VariantTypeInfo variant, TypeInfo armType) =>
        variant.Members.FirstOrDefault(predicate: m =>
            !m.IsNone && m.Type is not null &&
            (m.Type.FullName == armType.FullName || m.Type.Name == armType.Name));

    /// <summary>
    /// Builds: <c>when me { is None => return "None", is T as v => return v.represent(), ... }</c>.
    /// </summary>
    private static WhenStatement BuildVariantRepresentBody(VariantTypeInfo variant,
        TypeInfo textType)
    {
        var meRef = new IdentifierExpression(Name: "me", Location: _synthLoc)
        {
            ResolvedType = variant
        };

        var clauses = new List<WhenClause>(capacity: variant.Members.Count + 1);
        foreach (VariantMemberInfo member in variant.Members)
        {
            string memberName = member.IsNone
                ? "None"
                : member.Type!.Name;
            // IsNone = the absent arm (rendered as "none"). Zero-sized types like None or
            // an empty record are real values — render via the type's own represent (or the
            // type name when we can't bind a void payload).
            bool isAbsentArm = member.IsNone;
            bool isVoidPayload = !isAbsentArm && member.Type?.Name == "None";

            var typeExpr =
                new TypeExpression(Name: memberName, GenericArguments: null, Location: _synthLoc)
                {
                    ResolvedType = member.Type
                };

            Pattern pattern;
            Statement clauseBody;

            if (isAbsentArm)
            {
                pattern = new TypePattern(Type: typeExpr,
                    VariableName: null,
                    Bindings: null,
                    Location: _synthLoc);
                clauseBody = new ReturnStatement(
                    Value: new LiteralExpression(Value: $"{variant.ShortTypeName}(none)",
                        LiteralType: TokenType.TextLiteral,
                        Location: _synthLoc) { ResolvedType = textType },
                    Location: _synthLoc);
            }
            else if (isVoidPayload)
            {
                pattern = new TypePattern(Type: typeExpr,
                    VariableName: null,
                    Bindings: null,
                    Location: _synthLoc);
                clauseBody = new ReturnStatement(
                    Value: new LiteralExpression(Value: $"{variant.ShortTypeName}({memberName})",
                        LiteralType: TokenType.TextLiteral,
                        Location: _synthLoc) { ResolvedType = textType },
                    Location: _synthLoc);
            }
            else
            {
                pattern = new TypePattern(Type: typeExpr,
                    VariableName: "v",
                    Bindings: null,
                    Location: _synthLoc);

                var vRef = new IdentifierExpression(Name: "v", Location: _synthLoc)
                {
                    ResolvedType = member.Type
                };
                var representCall = new CallExpression(
                    Callee: new MemberExpression(Object: vRef,
                        MemberName: RepresentMemberRoutineName,
                        Location: _synthLoc) { ResolvedType = textType },
                    Arguments: [],
                    Location: _synthLoc) { ResolvedType = textType };

                var parts = new List<InsertedTextPart>
                {
                    new TextPart(Text: $"{variant.ShortTypeName}(", Location: _synthLoc),
                    new ExpressionPart(Expression: representCall,
                        FormatSpec: null,
                        Location: _synthLoc),
                    new TextPart(Text: ")", Location: _synthLoc)
                };
                var fstring =
                    new InsertedTextExpression(Parts: parts, IsRaw: false, Location: _synthLoc)
                    {
                        ResolvedType = textType
                    };
                clauseBody = new ReturnStatement(Value: fstring, Location: _synthLoc);
            }

            clauses.Add(new WhenClause(Pattern: pattern, Body: clauseBody, Location: _synthLoc));
        }

        clauses.Add(new WhenClause(
            Pattern: new ElsePattern(VariableName: null, Location: _synthLoc),
            Body: new ReturnStatement(
                Value: new LiteralExpression(Value: $"{variant.ShortTypeName}(<error>)",
                    LiteralType: TokenType.TextLiteral,
                    Location: _synthLoc) { ResolvedType = textType },
                Location: _synthLoc),
            Location: _synthLoc));

        return new WhenStatement(Expression: meRef, Clauses: clauses, Location: _synthLoc);
    }

    /// <summary>
    /// Builds:
    /// <c>when me { is None => return "Mod.V(type_id: 0, none)", is T as v => return f"Mod.V(type_id: N, {v.diagnose()})", ... }</c>.
    /// </summary>
    private static WhenStatement BuildVariantDiagnoseBody(VariantTypeInfo variant,
        TypeInfo textType)
    {
        var meRef = new IdentifierExpression(Name: "me", Location: _synthLoc)
        {
            ResolvedType = variant
        };

        var clauses = new List<WhenClause>(capacity: variant.Members.Count + 1);
        foreach (VariantMemberInfo member in variant.Members)
        {
            string memberName = member.IsNone
                ? "None"
                : member.Type!.Name;
            bool isAbsentArm = member.IsNone;
            bool isVoidPayload = !isAbsentArm && member.Type?.Name == "None";
            ulong typeId = isAbsentArm
                ? 0UL
                : TypeIdHelper.ComputeTypeId(fullName: member.Type!.FullName);

            var typeExpr =
                new TypeExpression(Name: memberName, GenericArguments: null, Location: _synthLoc)
                {
                    ResolvedType = member.Type
                };

            Pattern pattern;
            Statement clauseBody;

            if (isAbsentArm)
            {
                pattern = new TypePattern(Type: typeExpr,
                    VariableName: null,
                    Bindings: null,
                    Location: _synthLoc);
                string literal = $"{variant.QualifiedTypeName}(type_id: 0, none)";
                clauseBody = new ReturnStatement(
                    Value: new LiteralExpression(Value: literal,
                        LiteralType: TokenType.TextLiteral,
                        Location: _synthLoc) { ResolvedType = textType },
                    Location: _synthLoc);
            }
            else if (isVoidPayload)
            {
                pattern = new TypePattern(Type: typeExpr,
                    VariableName: null,
                    Bindings: null,
                    Location: _synthLoc);
                string literal = $"{variant.QualifiedTypeName}(type_id: {typeId}, {memberName})";
                clauseBody = new ReturnStatement(
                    Value: new LiteralExpression(Value: literal,
                        LiteralType: TokenType.TextLiteral,
                        Location: _synthLoc) { ResolvedType = textType },
                    Location: _synthLoc);
            }
            else
            {
                pattern = new TypePattern(Type: typeExpr,
                    VariableName: "v",
                    Bindings: null,
                    Location: _synthLoc);

                var vRef = new IdentifierExpression(Name: "v", Location: _synthLoc)
                {
                    ResolvedType = member.Type
                };
                var diagnoseCall = new CallExpression(
                    Callee: new MemberExpression(Object: vRef,
                        MemberName: DiagnoseMemberRoutineName,
                        Location: _synthLoc) { ResolvedType = textType },
                    Arguments: [],
                    Location: _synthLoc) { ResolvedType = textType };

                string prefix = $"{variant.QualifiedTypeName}(type_id: {typeId}, ";
                var parts = new List<InsertedTextPart>
                {
                    new TextPart(Text: prefix, Location: _synthLoc),
                    new ExpressionPart(Expression: diagnoseCall,
                        FormatSpec: null,
                        Location: _synthLoc),
                    new TextPart(Text: ")", Location: _synthLoc)
                };
                var fstring =
                    new InsertedTextExpression(Parts: parts, IsRaw: false, Location: _synthLoc)
                    {
                        ResolvedType = textType
                    };
                clauseBody = new ReturnStatement(Value: fstring, Location: _synthLoc);
            }

            clauses.Add(new WhenClause(Pattern: pattern, Body: clauseBody, Location: _synthLoc));
        }

        clauses.Add(new WhenClause(
            Pattern: new ElsePattern(VariableName: null, Location: _synthLoc),
            Body: new ReturnStatement(
                Value: new LiteralExpression(
                    Value: $"{variant.QualifiedTypeName}(type_id: <error>)",
                    LiteralType: TokenType.TextLiteral,
                    Location: _synthLoc) { ResolvedType = textType },
                Location: _synthLoc),
            Location: _synthLoc));

        return new WhenStatement(Expression: meRef, Clauses: clauses, Location: _synthLoc);
    }
}
