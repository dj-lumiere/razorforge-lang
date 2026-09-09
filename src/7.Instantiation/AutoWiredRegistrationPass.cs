using Compiler.Declaration;
using SyntaxTree;
using Compiler.Verification;
using Compiler.Verification.Enums;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;
using TypeSymbol = TypeModel.Types.TypeInfo;

namespace Compiler.Instantiation;

/// <summary>
/// Phase 6: Auto-registers builder-generated member routine signatures for all user types.
/// These are default routines that every type of a given category gets (hash(), eq(), etc.).
/// represent and diagnose are auto-registered (overridable).
/// Only registers if the user hasn't already defined the routine.
/// </summary>
/// <summary>
/// Bundles the resolved carrier types forwarded to per-type registration helpers
/// (<see cref="AutoWiredRegistrationPass.RegisterForType"/>,
/// <see cref="AutoWiredRegistrationPass.RegisterUniversalTypeParamRoutines"/>).
/// All members are nullable; a missing type suppresses the corresponding routine group.
/// </summary>
internal readonly record struct AutoWiredTypeBundle(
    TypeInfo? TextType,
    TypeInfo? BoolType,
    TypeInfo? U64Type,
    TypeInfo? S64Type,
    TypeInfo? NoneType,
    TypeInfo? SerialValueType,
    TypeInfo? ListDef,
    TypeInfo? ListTextType,
    TypeInfo? ListFieldInfoType,
    TypeInfo? ListProtocolInfoType,
    TypeInfo? ListRoutineInfoType,
    TypeInfo? ByteSizeType);

internal sealed class AutoWiredRegistrationPass
{
    private readonly TypeRegistry _registry;

    public AutoWiredRegistrationPass(TypeRegistry registry,
        HashSet<(string TypeName, string ProtocolName)>? implicitConformances = null)
    {
        _registry = registry;
        // implicitConformances is accepted for API compatibility but is not used internally;
        // everywhere-derive opt-in is driven entirely by the explicit obeyed-protocol closure.
        _ = implicitConformances;
    }

    public void Run(bool builderServiceImported = true)
    {
        // Look up required types (bail on each if not available)
        TypeSymbol? textType = _registry.LookupType(name: "Text");
        TypeSymbol? boolType = _registry.LookupType(name: "Bool");
        TypeSymbol? u64Type = _registry.LookupType(name: "U64");
        TypeSymbol? s64Type = _registry.LookupType(name: "S64");
        TypeSymbol? byteSizeType = _registry.LookupType(name: "ByteSize");
        TypeSymbol? noneType = _registry.LookupType(name: "None");
        // SerialValue backs the auto-derived serialize() (Serializable). Registered only on the
        // aggregate categories that obey Serializable (Record/Entity/Variant), mirroring how their
        // WiredRoutinePass bodies are synthesized — promise==body.
        TypeSymbol? serialValueType = _registry.LookupType(name: "SerialValue");

        // Look up List[T] for list-returning synthesized routines
        TypeSymbol? listDef = _registry.LookupType(name: "List");
        TypeSymbol? listTextType = listDef != null && textType != null
            ? _registry.GetOrCreateResolution(genericDef: listDef, typeArguments: [textType])
            : null;

        // BuilderQuery helper-type closures (List[FieldInfo], List[ProtocolInfo],
        // List[RoutineInfo]) are only resolved when the user program actually imports BuilderQuery.
        // Otherwise GMP would drag in the full BTreeListNode/Owned/Array/ArrayIterator closure for
        // every type via the metadata routines registered on each type.
        (TypeSymbol? listFieldInfoType, TypeSymbol? listProtocolInfoType,
                TypeSymbol? listRoutineInfoType) =
            ResolveBuilderInfoTypes(builderServiceImported: builderServiceImported,
                listDef: listDef);

        var bundle = new AutoWiredTypeBundle(TextType: textType,
            BoolType: boolType,
            U64Type: u64Type,
            S64Type: s64Type,
            NoneType: noneType,
            SerialValueType: serialValueType,
            ListDef: listDef,
            ListTextType: listTextType,
            ListFieldInfoType: listFieldInfoType,
            ListProtocolInfoType: listProtocolInfoType,
            ListRoutineInfoType: listRoutineInfoType,
            ByteSizeType: byteSizeType);

        foreach (TypeSymbol type in _registry.GetTypesWithMemberRoutines())
        {
            RegisterForType(type: type, bundle: bundle);
        }

        // Source location and caller standalone routines (injected at call site by codegen)
        BuilderInfoProvider.RegisterStandaloneRoutines(registry: _registry,
            textType: textType,
            s64Type: s64Type);

        // Synthesize BuilderQuery record type with platform/build info member routines
        BuilderInfoProvider.RegisterModuleRoutines(registry: _registry,
            textType: textType,
            u64Type: u64Type,
            s64Type: s64Type);

        // Auto-register Text.create(from: T) for all concrete user types so every type
        // structurally satisfies Representable[T].
        if (textType != null)
        {
            RegisterTextFromCreators(textType: textType);
        }

        // Register builder-service routines + represent/diagnose as universal member routines so
        // T.data_size(), K.type_id(), T.represent(), etc. resolve in generic function bodies.
        RegisterUniversalTypeParamRoutines(bundle: bundle);
    }

    /// <summary>
    /// Resolves the BuilderQuery list-wrapper types (List[FieldInfo], List[ProtocolInfo],
    /// List[RoutineInfo]) when the user program imports BuilderQuery, or returns nulls otherwise.
    /// Extracted from <see cref="Run"/> to reduce its cognitive complexity.
    /// </summary>
    private (TypeSymbol? ListFieldInfo, TypeSymbol? ListProtocolInfo, TypeSymbol? ListRoutineInfo)
        ResolveBuilderInfoTypes(bool builderServiceImported, TypeSymbol? listDef)
    {
        if (!builderServiceImported)
        {
            return (null, null, null);
        }

        // FieldInfo/ProtocolInfo/RoutineInfo live in module BuilderQuery — qualify the lookup.
        TypeSymbol? fieldInfoType = _registry.LookupType(name: "BuilderQuery.FieldInfo");
        TypeSymbol? protocolInfoType = _registry.LookupType(name: "BuilderQuery.ProtocolInfo");
        TypeSymbol? routineInfoType = _registry.LookupType(name: "BuilderQuery.RoutineInfo");

        TypeSymbol? listFieldInfoType = listDef != null && fieldInfoType != null
            ? _registry.GetOrCreateResolution(genericDef: listDef, typeArguments: [fieldInfoType])
            : null;
        TypeSymbol? listProtocolInfoType = listDef != null && protocolInfoType != null
            ? _registry.GetOrCreateResolution(genericDef: listDef,
                typeArguments: [protocolInfoType])
            : null;
        TypeSymbol? listRoutineInfoType = listDef != null && routineInfoType != null
            ? _registry.GetOrCreateResolution(genericDef: listDef,
                typeArguments: [routineInfoType])
            : null;

        return (listFieldInfoType, listProtocolInfoType, listRoutineInfoType);
    }

    /// <summary>
    /// Registers all auto-derived member routines for a single type. Contains the body of the
    /// main <see cref="Run"/> foreach loop, extracted to reduce cognitive complexity.
    /// </summary>
    private void RegisterForType(TypeSymbol type, AutoWiredTypeBundle bundle)
    {
        var existingMemberRoutines = _registry.GetMemberRoutinesForType(type: type)
                                              .ToList();

        // All types: represent(), diagnose() — auto-generated, overridable
        if (bundle.TextType != null)
        {
            MaybeRegisterWired(owner: type,
                name: RuntimeContract.Display.Represent,
                returnType: bundle.TextType,
                existingMemberRoutines: existingMemberRoutines);
            MaybeRegisterWired(owner: type,
                name: RuntimeContract.Display.Diagnose,
                returnType: bundle.TextType,
                existingMemberRoutines: existingMemberRoutines);
        }

        // Serializable: serialize() -> SerialValue is UNIVERSAL — every value has one so the derived
        // composite walk can call field.serialize() unconditionally (no obeying gate).
        if (bundle.SerialValueType != null && type.Category is TypeCategory.Record
                or TypeCategory.Entity or TypeCategory.Variant or TypeCategory.Choice
                or TypeCategory.Flags)
        {
            MaybeRegisterWired(owner: type,
                name: RuntimeContract.Serialize,
                returnType: bundle.SerialValueType,
                existingMemberRoutines: existingMemberRoutines);
        }

        // Unified destructor: every non-wrapper type gets a dangerous destroy().
        if (bundle.NoneType != null && !IsWrapperType(type: type))
        {
            MaybeRegisterDestroy(owner: type,
                noneType: bundle.NoneType,
                existingMemberRoutines: existingMemberRoutines);
        }

        // Cycle-collector per-type hooks for non-wrapper entities.
        if (bundle.NoneType != null && type.Category == TypeCategory.Entity &&
            !IsWrapperType(type: type))
        {
            MaybeRegisterRoamHook(owner: type,
                name: "roam_trace_impl",
                noneType: bundle.NoneType,
                existingMemberRoutines: existingMemberRoutines);
            MaybeRegisterRoamHook(owner: type,
                name: "roam_free_impl",
                noneType: bundle.NoneType,
                existingMemberRoutines: existingMemberRoutines);
        }

        // All types: BuilderQuery metadata routines
        BuilderInfoProvider.RegisterRoutinesOnType(type: type,
            existingMemberRoutines: existingMemberRoutines,
            registry: _registry,
            types: new BuilderQueryTypeSet(TextType: bundle.TextType,
                BoolType: bundle.BoolType,
                U64Type: bundle.U64Type,
                S64Type: bundle.S64Type,
                ListTextType: bundle.ListTextType,
                ListFieldInfoType: bundle.ListFieldInfoType,
                ListProtocolInfoType: bundle.ListProtocolInfoType,
                ListRoutineInfoType: bundle.ListRoutineInfoType,
                ByteSizeType: bundle.ByteSizeType));

        switch (type.Category)
        {
            case TypeCategory.Record:
                HandleRecordCategory(type: type,
                    u64Type: bundle.U64Type,
                    existingMemberRoutines: existingMemberRoutines);
                break;

            case TypeCategory.Entity:
                HandleEntityCategory(type: type, existingMemberRoutines: existingMemberRoutines);
                break;

            case TypeCategory.Choice:
                HandleChoiceCategory(type: type,
                    u64Type: bundle.U64Type,
                    boolType: bundle.BoolType,
                    s64Type: bundle.S64Type,
                    textType: bundle.TextType,
                    listDef: bundle.ListDef,
                    existingMemberRoutines: existingMemberRoutines);
                break;

            case TypeCategory.Crashable:
                HandleCrashableCategory(type: type,
                    textType: bundle.TextType,
                    existingMemberRoutines: existingMemberRoutines);
                break;

            case TypeCategory.Flags:
                HandleFlagsCategory(type: type,
                    u64Type: bundle.U64Type,
                    boolType: bundle.BoolType,
                    listDef: bundle.ListDef,
                    existingMemberRoutines: existingMemberRoutines);
                break;

            case TypeCategory.Variant:
                HandleVariantCategory(type: type,
                    textType: bundle.TextType,
                    existingMemberRoutines: existingMemberRoutines);
                break;
        }

        // Declaration-driven everywhere-derive registration.
        RegisterEverywhereDeriveMembers(type: type);
    }

    /// <summary>
    /// Auto-registers <c>Text.create(from: T)</c> for every concrete user-defined type so that
    /// every type structurally satisfies <c>Representable[T]</c>. Extracted from
    /// <see cref="Run"/> to reduce its cognitive complexity.
    /// </summary>
    private void RegisterTextFromCreators(TypeSymbol textType)
    {
        var textCreateMemberRoutines = _registry.GetMemberRoutinesForType(type: textType)
                                                .Where(predicate: m => m.IsCreator)
                                                .ToList();

        foreach (TypeSymbol type in _registry.GetAllTypes())
        {
            if (type.Category is not (TypeCategory.Record or TypeCategory.Entity
                or TypeCategory.Choice or TypeCategory.Flags or TypeCategory.Variant))
            {
                continue;
            }

            // Skip generic-definition types and WrapperTypeInfo definitions — registering a
            // create(from: T) for the bare wrapper produces a phantom Text.create(Core.Owned) symbol
            // that overload-resolution can drift onto, then the linker fails (no definition emitted).
            if (type.IsGenericDefinition || type is WrapperTypeInfo)
            {
                continue;
            }

            bool alreadyDefined = textCreateMemberRoutines.Any(predicate: m =>
                m.Parameters.Count == 1 && m.Parameters[index: 0].Type.FullName == type.FullName);
            if (alreadyDefined)
            {
                continue;
            }

            _registry.RegisterRoutine(routine: new RoutineInfo(name: RoutineInfo.CreatorName)
            {
                Kind = RoutineKind.Creator,
                OwnerType = textType,
                Parameters = [new ParameterInfo(name: "from", type: type)],
                ReturnType = textType,
                IsFailable = false,
                DeclaredMutation = MutationCategory.Readonly,
                MutationCategory = MutationCategory.Readonly,
                Visibility = VisibilityModifier.Open,
                IsSynthesized = true
            });
        }
    }

    /// <summary>
    /// Registers builder-service routines and display/lifecycle routines on the universal
    /// generic-parameter type <c>T</c>, so these resolve in any generic function body.
    /// Extracted from <see cref="Run"/> to reduce its cognitive complexity.
    /// </summary>
    private void RegisterUniversalTypeParamRoutines(AutoWiredTypeBundle bundle)
    {
        var tParam = new GenericParameterTypeInfo(name: "T");
        var universalExisting = new List<RoutineInfo>();
        BuilderInfoProvider.RegisterRoutinesOnType(type: tParam,
            existingMemberRoutines: universalExisting,
            registry: _registry,
            types: new BuilderQueryTypeSet(TextType: bundle.TextType,
                BoolType: bundle.BoolType,
                U64Type: bundle.U64Type,
                S64Type: bundle.S64Type,
                ListTextType: bundle.ListTextType,
                ListFieldInfoType: bundle.ListFieldInfoType,
                ListProtocolInfoType: bundle.ListProtocolInfoType,
                ListRoutineInfoType: bundle.ListRoutineInfoType,
                ByteSizeType: bundle.ByteSizeType));
        if (bundle.TextType != null)
        {
            MaybeRegisterWired(owner: tParam,
                name: RuntimeContract.Display.Represent,
                returnType: bundle.TextType,
                existingMemberRoutines: universalExisting);
            MaybeRegisterWired(owner: tParam,
                name: RuntimeContract.Display.Diagnose,
                returnType: bundle.TextType,
                existingMemberRoutines: universalExisting);
        }

        // destroy as a universal member routine so v.destroy() resolves on a generic T.
        if (bundle.NoneType != null)
        {
            MaybeRegisterDestroy(owner: tParam,
                noneType: bundle.NoneType,
                existingMemberRoutines: universalExisting);
        }
    }

    /// <summary>
    /// Registers the auto-derived member routines for a <see cref="TypeCategory.Record"/> type.
    /// </summary>
    private void HandleRecordCategory(TypeSymbol type, TypeSymbol? u64Type,
        List<RoutineInfo> existingMemberRoutines)
    {
        // None maps to LLVM void — it cannot appear as a parameter type.
        // Skip comparison/hash/copy stubs; two Nones are trivially equal.
        // Wrapper types (Retained, Viewing, etc.) are transparent forwarders —
        // WrapperForwardingPass lazily synthesizes their hash/eq/cmp from the inner T.
        // Don't register field-based stubs here: for zero-field wrappers (T)
        // WiredRoutinePass would generate wrong bodies (returns 0 / returns true).
        bool isWrapper = type is RecordTypeInfo &&
                         WrapperForwardingPass.WrapperTypeNames.Contains(
                             item: (type as RecordTypeInfo)?.GenericDefinition?.Name ?? type.Name);
        // DECISION (2026-06-14): records do NOT auto-derive eq / hash. `obeys Equatable`
        // / `Hashable` on a record is a PROMISE the author fulfils by HAND-WRITING the
        // memberRoutine — field-delegated synthesis is fragile (breaks when a field type lacks the
        // memberRoutine, e.g. an Atomic / lock-flag field) and is semantically wrong for opaque /
        // container types whose logical value is not their field tuple. Auto eq / hash is
        // reserved for tuple / choice / flags (simple, unambiguous tag/element compare). The
        // stdlib's equatable/hashable struct records (Complex, Integer, Decimal, C32/64/128)
        // already hand-write these. store (below) + represent / diagnose stay auto-derived.

        // `assign` (Assignable) / `copy` (Copyable) are now registered by the declaration-driven
        // everywhere-derive loop (RegisterEverywhereDeriveMembers) — the `needs P everywhere` rule
        // read straight from the protocol, opt-in via `obeys P`, replacing this per-protocol
        // hardcode. A type that must be assignable/copyable declares `obeys Assignable`/`Copyable`.

        // `eq` (Equatable) is now registered by the declaration-driven everywhere-derive loop
        // (RegisterEverywhereDeriveMembers) — the `needs Equatable everywhere` rule read straight
        // from the protocol, opt-in + all-members-Equatable, replacing this per-protocol hardcode.
        // `hash` (Hashable) stays below: its keyed `hash(k0, k1)` form is not a field-walk derive.

        // `Hashable` requires ONLY the keyed `hash(k0, k1)` (what Set/Dict use); there is no
        // 0-arg `hash()` on value types (scalars supply only the keyed form), so field-walking
        // a 0-arg field hash would be undefined. Register just the keyed hash.
        if (!type.IsNone && !isWrapper && u64Type != null &&
            ObeysProtocol(type: type, protocolName: "Hashable"))
        {
            MaybeRegisterKeyedHash(owner: type,
                u64Type: u64Type,
                existingMemberRoutines: existingMemberRoutines);
        }
    }

    /// <summary>
    /// Registers the auto-derived member routines for a <see cref="TypeCategory.Entity"/> type.
    /// </summary>
    private void HandleEntityCategory(TypeSymbol type, List<RoutineInfo> existingMemberRoutines)
    {
        // DECISION (2026-06-14): entities do NOT auto-derive eq either. An entity is an
        // identity/reference type whose logical value is rarely its field tuple (e.g. a
        // collection's value is its elements, not its buffer pointer + counts), so
        // field-delegated equality is the wrong default. Entities that want equality declare
        // `eq` explicitly with the right semantics. (No stdlib entity obeys Equatable.)

        // Synthesize create(field1: T1, ...) -> EntityType for field construction.
        // Always synthesize the all-fields overload unless an exact match already exists,
        // so field construction inside user-defined create overloads works too.
        // Skip generic definitions (their resolved instances get synthesis).
        if (type is EntityTypeInfo entityForCreate && !type.IsGenericDefinition &&
            !existingMemberRoutines.Any(predicate: m =>
                m.IsCreator && m.Parameters.Count == entityForCreate.MemberVariables.Count &&
                entityForCreate.MemberVariables
                               .Select(selector: mv => mv.Name)
                               .SequenceEqual(
                                    second: m.Parameters.Select(selector: p => p.Name)) &&
                // An overload's identity is its PARAMETER TYPES, not just names — a user
                // `create(tag: S32)` must NOT suppress the all-fields memberwise `create(tag: S64)`
                // when the field type differs, else field construction inside that user create
                // (`Tracer(tag: S64(...))`) finds no matching creator.
                entityForCreate.MemberVariables
                               .Select(selector: mv => mv.Type.FullName)
                               .SequenceEqual(
                                    second: m.Parameters.Select(selector: p => p.Type.FullName))))
        {
            _registry.RegisterRoutine(routine: new RoutineInfo(name: RoutineInfo.CreatorName)
            {
                Kind = RoutineKind.Creator,
                OwnerType = type,
                Parameters = entityForCreate.MemberVariables
                                            .Select(selector: mv =>
                                                 new ParameterInfo(name: mv.Name, type: mv.Type))
                                            .ToList(),
                ReturnType = type,
                IsFailable = false,
                DeclaredMutation = MutationCategory.Readonly,
                MutationCategory = MutationCategory.Readonly,
                Visibility = VisibilityModifier.Open,
                IsSynthesized = true
            });
        }
    }

    /// <summary>
    /// Registers the auto-derived member routines for a <see cref="TypeCategory.Choice"/> type.
    /// </summary>
    private void HandleChoiceCategory(TypeSymbol type, TypeSymbol? u64Type, TypeSymbol? boolType,
        TypeSymbol? s64Type, TypeSymbol? textType, TypeSymbol? listDef,
        List<RoutineInfo> existingMemberRoutines)
    {
        // Choices/flags get eq/hash unconditionally — equality is unambiguous
        // tag-compare with no field-selection design choice to make. Stdlib's
        // ComparisonSign and BuilderQuery enums rely on this for represent /
        // diagnose / derived comparison operators.
        if (u64Type != null)
        {
            MaybeRegisterWired(owner: type,
                name: "hash",
                returnType: u64Type,
                existingMemberRoutines: existingMemberRoutines);
            MaybeRegisterKeyedHash(owner: type,
                u64Type: u64Type,
                existingMemberRoutines: existingMemberRoutines);
        }

        if (boolType != null)
        {
            MaybeRegisterWiredWithParam(owner: type,
                name: "eq",
                paramName: "you",
                paramType: type,
                returnType: boolType,
                existingMemberRoutines: existingMemberRoutines);
        }

        // Choices auto-derive Assignable (scalar tag layout).
        MaybeRegisterWired(owner: type,
            name: "assign",
            returnType: type,
            existingMemberRoutines: existingMemberRoutines);
        MaybeRegisterWired(owner: type,
            name: "duplicate",
            returnType: type,
            existingMemberRoutines: existingMemberRoutines);

        // S64.create(from: ChoiceType) — choice_val.S64() desugars to S64.create(from: choice_val)
        if (s64Type != null && !type.IsGenericDefinition &&
            _registry.LookupCreatorOverload(type: s64Type, argTypes: [type]) == null)
        {
            _registry.RegisterRoutine(routine: new RoutineInfo(name: RoutineInfo.CreatorName)
            {
                Kind = RoutineKind.Creator,
                OwnerType = s64Type,
                Parameters = [new ParameterInfo(name: "from", type: type)],
                ReturnType = s64Type,
                IsFailable = false,
                DeclaredMutation = MutationCategory.Readonly,
                MutationCategory = MutationCategory.Readonly,
                Visibility = VisibilityModifier.Open,
                IsSynthesized = true
            });
        }

        // S32.create(from: ChoiceType) — a choice is S32-backed, so this reinterprets its discriminant
        // to S32 (no width change). The choice `eq` derive + choice pattern lowering use it to compare
        // discriminants via S32.eq (icmp eq i32), so the `is` operator never reaches codegen.
        TypeSymbol? s32ChoiceType = _registry.LookupType(name: "S32");
        if (s32ChoiceType != null && !type.IsGenericDefinition &&
            _registry.LookupCreatorOverload(type: s32ChoiceType, argTypes: [type]) == null)
        {
            _registry.RegisterRoutine(routine: new RoutineInfo(name: RoutineInfo.CreatorName)
            {
                Kind = RoutineKind.Creator,
                OwnerType = s32ChoiceType,
                Parameters = [new ParameterInfo(name: "from", type: type)],
                ReturnType = s32ChoiceType,
                IsFailable = false,
                DeclaredMutation = MutationCategory.Readonly,
                MutationCategory = MutationCategory.Readonly,
                Visibility = VisibilityModifier.Open,
                IsSynthesized = true
            });
        }

        if (textType != null)
        {
            // name stays bare `create` + IsFailable (set by the helper); the `!` is a
            // STRUCTURED flag, never baked into the Name. A `.create!(…)` call resolves
            // against "create" and the `from: Text` param disambiguates.
            MaybeRegisterWiredFailable(owner: type,
                name: RoutineInfo.CreatorName,
                returnType: type,
                existingMemberRoutines: existingMemberRoutines,
                param: ("from", textType),
                kind: RoutineKind.Creator);
        }

        if (listDef != null)
        {
            TypeSymbol listMeType = _registry.GetOrCreateResolution(
                genericDef: listDef,
                typeArguments: [type]);
            MaybeRegisterWired(owner: type,
                name: "all_cases",
                returnType: listMeType,
                existingMemberRoutines: existingMemberRoutines);
        }

        // count() — number of declared cases.
        if (u64Type != null)
        {
            MaybeRegisterWired(owner: type,
                name: "count",
                returnType: u64Type,
                existingMemberRoutines: existingMemberRoutines);
        }

        // from-S32 reverse constructor (build a choice value from its discriminant, used by the all_cases
        // derive to reconstruct each case from `$valueof(c)`). Mirrors the forward `S64.create(from: choice)`.
        TypeSymbol? s32Type = _registry.LookupType(name: "S32");
        if (s32Type != null && !type.IsGenericDefinition &&
            _registry.LookupCreatorOverload(type: type, argTypes: [s32Type]) == null)
        {
            _registry.RegisterRoutine(routine: new RoutineInfo(name: RoutineInfo.CreatorName)
            {
                Kind = RoutineKind.Creator,
                OwnerType = type,
                Parameters = [new ParameterInfo(name: "from", type: s32Type)],
                ReturnType = type,
                IsFailable = false,
                DeclaredMutation = MutationCategory.Readonly,
                MutationCategory = MutationCategory.Readonly,
                Visibility = VisibilityModifier.Open,
                IsSynthesized = true
            });
        }
    }

    /// <summary>
    /// Registers the auto-derived member routines for a <see cref="TypeCategory.Crashable"/> type.
    /// </summary>
    private void HandleCrashableCategory(TypeSymbol type, TypeSymbol? textType,
        List<RoutineInfo> existingMemberRoutines)
    {
        // crash_title() is @generated — synthesized from type name, overridable
        if (textType != null)
        {
            MaybeRegisterWired(owner: type,
                name: "crash_title",
                returnType: textType,
                existingMemberRoutines: existingMemberRoutines);
            // crash_message() has a default too — a crashable that declares no explicit
            // crash_message (e.g. `crashable BareErr`) still needs a concrete body, or the
            // throw path resolves to the abstract `Crashable.crash_message()` protocol
            // requirement and codegen over-prunes it ("declared and called but never
            // defined"). Default body is `return me.crash_title()` (synthesized below).
            MaybeRegisterWired(owner: type,
                name: RuntimeContract.CrashMessage,
                returnType: textType,
                existingMemberRoutines: existingMemberRoutines);
        }

        // Synthesize create(field1: T1, ...) -> CrashableType for construction via throw
        if (type is CrashableTypeInfo crashableForCreate &&
            !existingMemberRoutines.Any(predicate: m => m.IsCreator))
        {
            _registry.RegisterRoutine(routine: new RoutineInfo(name: RoutineInfo.CreatorName)
            {
                Kind = RoutineKind.Creator,
                OwnerType = type,
                Parameters = crashableForCreate.MemberVariables
                                               .Select(selector: mv =>
                                                    new ParameterInfo(name: mv.Name,
                                                        type: mv.Type))
                                               .ToList(),
                ReturnType = type,
                IsFailable = false,
                DeclaredMutation = MutationCategory.Readonly,
                MutationCategory = MutationCategory.Readonly,
                Visibility = VisibilityModifier.Open,
                IsSynthesized = true
            });
        }

        // Auto-add Crashable protocol conformance (implicit from the crashable keyword)
        TypeSymbol? crashableProto = _registry.LookupType(name: "Crashable");
        if (crashableProto != null && type is CrashableTypeInfo crashableInfo &&
            crashableInfo.ImplementedProtocols.All(predicate: p => p.Name != "Crashable"))
        {
            var protocols = crashableInfo.ImplementedProtocols.ToList();
            protocols.Add(item: crashableProto);
            _registry.UpdateCrashableProtocols(typeName: type.FullName, protocols: protocols);
        }
    }

    /// <summary>
    /// Registers the auto-derived member routines for a <see cref="TypeCategory.Flags"/> type.
    /// </summary>
    private void HandleFlagsCategory(TypeSymbol type, TypeSymbol? u64Type, TypeSymbol? boolType,
        TypeSymbol? listDef, List<RoutineInfo> existingMemberRoutines)
    {
        // See Choice case above — equality is unambiguous bit-compare; always-on.
        if (u64Type != null)
        {
            MaybeRegisterWired(owner: type,
                name: "hash",
                returnType: u64Type,
                existingMemberRoutines: existingMemberRoutines);
            MaybeRegisterKeyedHash(owner: type,
                u64Type: u64Type,
                existingMemberRoutines: existingMemberRoutines);
        }

        if (boolType != null)
        {
            MaybeRegisterWiredWithParam(owner: type,
                name: "eq",
                paramName: "you",
                paramType: type,
                returnType: boolType,
                existingMemberRoutines: existingMemberRoutines);
        }

        // Bitwise combinators (`a and b`/`a but b` lower to bitor/bitand; `bitxor` for
        // symmetry). WiredRoutinePass.HandleFlags synthesizes the bodies as @llvm_ir
        // intrinsic calls on the underlying i64 repr; OperatorLoweringPass then lowers
        // `BitwiseOr`/`BitwiseAnd`/`BitwiseXor` on a Flags receiver to these calls.
        foreach (string bitOp in new[]
                 {
                     "bitand",
                     "bitor",
                     "bitxor"
                 })
        {
            MaybeRegisterWiredWithParam(owner: type,
                name: bitOp,
                paramName: "you",
                paramType: type,
                returnType: type,
                existingMemberRoutines: existingMemberRoutines);
        }

        // Flags auto-derive Assignable (scalar bitset layout).
        MaybeRegisterWired(owner: type,
            name: "assign",
            returnType: type,
            existingMemberRoutines: existingMemberRoutines);
        MaybeRegisterWired(owner: type,
            name: "duplicate",
            returnType: type,
            existingMemberRoutines: existingMemberRoutines);

        // U64.create(from: FlagsType) — flags_val.U64() desugars to U64.create(from: flags_val)
        if (u64Type != null && !type.IsGenericDefinition &&
            _registry.LookupCreatorOverload(type: u64Type, argTypes: [type]) == null)
        {
            _registry.RegisterRoutine(routine: new RoutineInfo(name: RoutineInfo.CreatorName)
            {
                Kind = RoutineKind.Creator,
                OwnerType = u64Type,
                Parameters = [new ParameterInfo(name: "from", type: type)],
                ReturnType = u64Type,
                IsFailable = false,
                DeclaredMutation = MutationCategory.Readonly,
                MutationCategory = MutationCategory.Readonly,
                Visibility = VisibilityModifier.Open,
                IsSynthesized = true
            });
        }

        MaybeRegisterWired(owner: type,
            name: "all_on",
            returnType: type,
            existingMemberRoutines: existingMemberRoutines);
        MaybeRegisterWired(owner: type,
            name: "all_off",
            returnType: type,
            existingMemberRoutines: existingMemberRoutines);
        if (listDef != null)
        {
            TypeSymbol listMeType = _registry.GetOrCreateResolution(
                genericDef: listDef,
                typeArguments: [type]);
            MaybeRegisterWired(owner: type,
                name: "all_cases",
                returnType: listMeType,
                existingMemberRoutines: existingMemberRoutines);
        }

        // count() — number of declared members; from-U64 reverse constructor (build a flags value from its
        // bitmask, used by the all_cases derive to reconstruct each member from `$valueof(c)`).
        if (u64Type != null)
        {
            MaybeRegisterWired(owner: type,
                name: "count",
                returnType: u64Type,
                existingMemberRoutines: existingMemberRoutines);
            if (!type.IsGenericDefinition &&
                _registry.LookupCreatorOverload(type: type, argTypes: [u64Type]) == null)
            {
                _registry.RegisterRoutine(routine: new RoutineInfo(name: RoutineInfo.CreatorName)
                {
                    Kind = RoutineKind.Creator,
                    OwnerType = type,
                    Parameters = [new ParameterInfo(name: "from", type: u64Type)],
                    ReturnType = type,
                    IsFailable = false,
                    DeclaredMutation = MutationCategory.Readonly,
                    MutationCategory = MutationCategory.Readonly,
                    Visibility = VisibilityModifier.Open,
                    IsSynthesized = true
                });
            }
        }
    }

    /// <summary>
    /// Registers the auto-derived member routines for a <see cref="TypeCategory.Variant"/> type.
    /// </summary>
    private void HandleVariantCategory(TypeSymbol type, TypeSymbol? textType,
        List<RoutineInfo> existingMemberRoutines)
    {
        // Variants get auto-synthesized represent/diagnose so user-defined
        // tagged unions render in f-strings and show calls without manual impls.
        // The wired-routine synthesis pass builds the bodies from the variant member list.
        // Registration here makes the stubs visible to overload resolution and the
        // reachability sweep so the symbols actually get emitted by codegen.
        if (textType != null && !type.IsGenericDefinition)
        {
            MaybeRegisterWired(owner: type,
                name: RuntimeContract.Display.Represent,
                returnType: textType,
                existingMemberRoutines: existingMemberRoutines);
            MaybeRegisterWired(owner: type,
                name: RuntimeContract.Display.Diagnose,
                returnType: textType,
                existingMemberRoutines: existingMemberRoutines);
        }

        // A variant with a destructible arm (a heap/managed payload that double-frees on
        // bitwise alias) gets a synthesized deep `copy` — WiredRoutinePass.BuildVariantCopyBody
        // reconstructs each such arm with `arm.copy()`. Registering it here makes the symbol
        // visible to overload resolution + the reachability sweep, and lets GetLifecycle return
        // it as the variant's retaining Copy so copy-lowering injects it at every copy point.
        if (!type.IsGenericDefinition && type is VariantTypeInfo variantForCopy &&
            _registry.VariantHasDestructibleArm(variant: variantForCopy))
        {
            MaybeRegisterWired(owner: type,
                name: "duplicate",
                returnType: type,
                existingMemberRoutines: existingMemberRoutines);
        }

        // A variant whose every arm is Assignable gets a shallow `assign` (arm-walk re-store).
        // REQUIRED so a variant used as a record member (e.g. `Maybe[S32]` in a struct) resolves
        // the field-walk `.assign()` its owner's auto-derived `assign` emits — the everywhere-
        // derive for records/entities is arm-blind, so variants register here. Gate on the
        // STRUCTURAL `EverywhereObeys` (branchof over THIS instance's concrete arms), NOT the
        // conferred `obeys` — a monomorphized instance (`Maybe[S32]`) is created AFTER conferral
        // ran, so it never gained the conferred `obeys Assignable`. Body: the `T.assign() needs T
        // is VariantType` derive template (branchof re-store).
        if (!type.IsGenericDefinition && type is VariantTypeInfo &&
            _registry.EverywhereObeys(type: type, protocol: "Assignable"))
        {
            MaybeRegisterWired(owner: type,
                name: "assign",
                returnType: type,
                existingMemberRoutines: existingMemberRoutines);
        }

        // Bidirectional per-arm constructors, auto-generated for every variant:
        //   V.create(from: Arm)  -> V    — box a branch value into the variant.
        //   Arm.create!(from: V) -> Arm  — failable extraction (absent when the active arm
        //                                   is not this one). The `from:` param type (not the
        //                                   arm name) carries the overload, so no RF-S770 clash
        //                                   with a same-named type (e.g. the `List` arm).
        if (!type.IsGenericDefinition && type is VariantTypeInfo variantForCtor)
        {
            RegisterVariantArmConstructors(variant: variantForCtor);
        }
    }

    /// <summary>
    /// Registers a no-parameter readonly wired routine if not already defined.
    /// </summary>
    private void MaybeRegisterWired(TypeSymbol owner, string name, TypeSymbol returnType,
        List<RoutineInfo> existingMemberRoutines)
    {
        if (existingMemberRoutines.Any(predicate: m => m.Name == name))
        {
            return;
        }

        _registry.RegisterRoutine(routine: new RoutineInfo(name: name)
        {
            Kind = RoutineKind.MemberRoutine,
            OwnerType = owner,
            Parameters = [],
            ReturnType = returnType,
            IsFailable = false,
            DeclaredMutation = MutationCategory.Readonly,
            MutationCategory = MutationCategory.Readonly,
            Visibility = VisibilityModifier.Open,
            IsSynthesized = true
        });
    }

    /// <summary>
    /// Registers the auto-derived <c>destroy()</c> destructor if not already user-defined.
    /// Marked <c>dangerous</c>: calling it (explicitly or overriding it) is manual memory
    /// management. The body is synthesized by <see cref="WiredRoutinePass"/>.
    /// </summary>
    private void MaybeRegisterDestroy(TypeSymbol owner, TypeSymbol noneType,
        List<RoutineInfo> existingMemberRoutines)
    {
        if (existingMemberRoutines.Any(predicate: m => m.Name == "destroy"))
        {
            return;
        }

        _registry.RegisterRoutine(routine: new RoutineInfo(name: "destroy")
        {
            Kind = RoutineKind.MemberRoutine,
            OwnerType = owner,
            Parameters = [],
            ReturnType = noneType,
            IsFailable = false,
            IsDangerous = true,
            DeclaredMutation = MutationCategory.Readonly,
            MutationCategory = MutationCategory.Readonly,
            Visibility = VisibilityModifier.Open,
            IsSynthesized = true
        });
    }

    /// <summary>
    /// Registers a cycle-collector hook memberRoutine (<c>roam_trace_impl</c> / <c>roam_free_impl</c>) if
    /// not already user-defined. Marked <c>dangerous</c> (raw controller/pointer work). No params,
    /// void return; the body is synthesized by <see cref="WiredRoutinePass"/>.
    /// </summary>
    private void MaybeRegisterRoamHook(TypeSymbol owner, string name, TypeSymbol noneType,
        List<RoutineInfo> existingMemberRoutines)
    {
        if (existingMemberRoutines.Any(predicate: m => m.Name == name))
        {
            return;
        }

        _registry.RegisterRoutine(routine: new RoutineInfo(name: name)
        {
            Kind = RoutineKind.MemberRoutine,
            OwnerType = owner,
            Parameters = [],
            ReturnType = noneType,
            IsFailable = false,
            IsDangerous = true,
            DeclaredMutation = MutationCategory.Readonly,
            MutationCategory = MutationCategory.Readonly,
            Visibility = VisibilityModifier.Open,
            IsSynthesized = true
        });
    }

    /// <summary>
    /// True for RC wrapper types (Retained/Tracked/Viewing/Modifying/Hijacked/...) — they
    /// supply their own custom destructor / forwarders and are excluded from generated `destroy`.
    /// </summary>
    private static bool IsWrapperType(TypeSymbol type)
    {
        string baseName = type switch
        {
            WrapperTypeInfo w => w.Name,
            RecordTypeInfo { GenericDefinition: { } d } => d.Name,
            _ => type.BareName
        };
        return WrapperForwardingPass.WrapperTypeNames.Contains(item: baseName);
    }

    /// <summary>
    /// Registers the keyed `hash(k0: U64, k1: U64) -> U64` overload if not already defined.
    /// Distinct from the unkeyed `hash()` by parameter count, so both can coexist.
    /// </summary>
    private void MaybeRegisterKeyedHash(TypeSymbol owner, TypeSymbol u64Type,
        List<RoutineInfo> existingMemberRoutines)
    {
        if (existingMemberRoutines.Any(predicate: m => m is { Name: "hash", Parameters.Count: 2 }))
        {
            return;
        }

        _registry.RegisterRoutine(routine: new RoutineInfo(name: "hash")
        {
            Kind = RoutineKind.MemberRoutine,
            OwnerType = owner,
            Parameters =
            [
                new ParameterInfo(name: "k0", type: u64Type),
                new ParameterInfo(name: "k1", type: u64Type)
            ],
            ReturnType = u64Type,
            IsFailable = false,
            DeclaredMutation = MutationCategory.Readonly,
            MutationCategory = MutationCategory.Readonly,
            Visibility = VisibilityModifier.Open,
            IsSynthesized = true
        });
    }

    /// <summary>Adds <paramref name="proto"/> and its full transitive parent chain to
    /// <paramref name="into"/> (canonical registry instances), keyed by name.</summary>
    private void AddProtocolAndParents(ProtocolTypeInfo proto,
        Dictionary<string, ProtocolTypeInfo> into)
    {
        // Resolve the canonical instance so GenericConstraints / MemberRoutines / ParentProtocols are
        // populated (an ImplementedProtocols / ParentProtocols entry may be a lightweight reference).
        ProtocolTypeInfo canonical =
            _registry.LookupType(name: proto.Name) as ProtocolTypeInfo ?? proto;
        if (!into.TryAdd(key: canonical.Name, value: canonical))
        {
            return;
        }

        foreach (ProtocolTypeInfo parent in canonical.ParentProtocols)
        {
            AddProtocolAndParents(proto: parent, into: into);
        }
    }

    private void RegisterEverywhereDeriveMembers(TypeSymbol type)
    {
        if (type.IsNone || IsWrapperType(type: type))
        {
            return;
        }

        // A GENERIC DEFINITION (Maybe[T]/Result[T]/user generic obeying P) IS processed: registering the
        // derive on the DEF is what lets GMP monomorphize it onto each concrete instance. Its everywhere
        // condition is DEFERRED to instantiation (a generic-param member obeys P only for a concrete arg),
        // so the EverywhereObeys gate below is skipped for a def — the def carries the derive's
        // "needs T obeys P" constraint, and a non-conforming instance simply never emits a used body.
        bool isGenericDef = type.IsGenericDefinition;

        // memberCount drives the 0-memvar rule below (per member, not a whole-type skip).
        int memberCount = type switch
        {
            RecordTypeInfo r => r.MemberVariables?.Count ?? 0,
            EntityTypeInfo e => e.MemberVariables?.Count ?? 0,
            _ => 0
        };

        // Record/Choice/Flags/Crashable all carry their implemented-protocol list on the record base.
        // Entities carry it on the entity type. Mirrors the protocol conformance analyzer's logic.
        List<TypeSymbol> obeyed = type switch
        {
            RecordTypeInfo r => r.ImplementedProtocols,
            EntityTypeInfo e => e.ImplementedProtocols,
            _ => []
        };

        // Every obeyed protocol + its parent chain. The structural-vs-opt-in split is enforced by the
        // CONFERRAL layer, not here: Assignable/Copyable are auto-conferred (so a value record obeys them
        // and gets assign/copy), while Equatable/Comparable/Hashable are NOT auto-conferred (so only a type
        // that explicitly `obeys` them appears here and gets eq/cmp/hash). Marker protocols (RecordType/…)
        // also appear but are filtered below — they carry no `everywhere` self-constraint.
        var explicitClosure =
            new Dictionary<string, ProtocolTypeInfo>(comparer: StringComparer.Ordinal);
        foreach (TypeSymbol protoRef in obeyed)
        {
            if (_registry.LookupType(name: protoRef.Name) is ProtocolTypeInfo obeyedProto)
            {
                AddProtocolAndParents(proto: obeyedProto, into: explicitClosure);
            }
        }

        foreach (ProtocolTypeInfo p in explicitClosure.Values)
        {
            if (p.GenericConstraints?.Any(predicate: c =>
                    c.ConstraintType == ConstraintKind.Everywhere) != true)
            {
                continue;
            }

            // The `everywhere` structural gate is evaluated PER-MEMBER below, not for the whole protocol:
            // it applies only to field-walk BASE derives (cmp/eq/hash/assign), NOT to delegation derives
            // (lt/le/gt/ge), which simply call the type's own base op and are valid whenever the type obeys
            // P (already true — p came from the obeyed-protocol closure).
            bool everywhereObeys =
                isGenericDef || _registry.EverywhereObeys(type: type, protocol: p.Name);

            foreach (ProtocolMemberRoutineInfo member in p.MemberRoutines)
            {
                RegisterEverywhereDeriveMember(type: type,
                    member: member,
                    memberCount: memberCount,
                    everywhereObeys: everywhereObeys);
            }
        }
    }

    /// <summary>
    /// Determines whether a single everywhere-derive protocol member should be skipped for
    /// <paramref name="type"/>. Returns true when the member is not a valid wired derive candidate,
    /// when the everywhere-obeys gate fails, when the 0-memvar rule fires, or when the type already
    /// provides a concrete implementation. Extracted from
    /// <see cref="RegisterEverywhereDeriveMembers"/> to reduce its cognitive complexity.
    /// </summary>
    private bool ShouldSkipEverywhereDeriveMember(TypeSymbol type,
        ProtocolMemberRoutineInfo member, int memberCount, bool everywhereObeys,
        out bool isDerivedOperator)
    {
        isDerivedOperator = false;

        // The catalog is the declarative source for whether a protocol member is a wired derive
        // and whether it is a BASE derive or a DERIVED operator: a derived operator's
        // `CapabilityWired` points at its base (≠ its own name); a base derive's points at itself.
        if (member.HasDefaultImplementation || !member.IsInstanceMemberRoutine ||
            !WiredRoutineCatalog.TryGet(name: member.Name, entry: out WiredEntry we))
        {
            return true;
        }

        // BASE derive (cmp/eq/hash/assign — CapabilityWired == own name) vs DERIVED operator
        // (lt/le/gt/ge from cmp, ne from eq, notcontains from contains — CapabilityWired == base).
        isDerivedOperator = we.CapabilityWired != member.Name;
        int memberArity = member.ParameterTypes.Count;

        if (isDerivedOperator)
        {
            // A DERIVED operator registers here ONLY when a universal derive-template body exists for
            // it (DeriveText.rf lt/le/gt/ge → `me.cmp(you) == ME_SMALL` etc.). Routing these through
            // the everywhere-derive template mechanism (registered + materialized by the collector) is
            // what makes `a < b` resolve uniformly for Character/numerics/records — the C#
            // DerivedOperatorPass bodies did not survive the collector/warm codegen path. Delegation
            // derives WITHOUT a template (ne, notcontains) are still produced by DerivedOperatorPass,
            // so we skip them here rather than register stubs the collector cannot materialize.
            return _registry.GetDeriveTemplate(name: member.Name,
                arity: memberArity,
                forType: type) is null;
        }

        // A field-walk BASE derive is only VALID when every member obeys P (its body field-walks into
        // `member.cmp/eq/…`). A concrete type that declares `obeys P` but fails this is a conformance
        // error surfaced elsewhere; we don't fabricate an ill-typed body. A generic DEF defers to
        // instantiation (everywhereObeys is forced true for a def above).
        if (!everywhereObeys)
        {
            return true;
        }

        // 0-memvar rule (per member, not per type): a field-less type (an `@llvm` scalar like U64,
        // an empty record, choice/flags) has no members to walk, so a FIELD-WALK BASE derive
        // (eq/cmp/hash → returns Bool/ComparisonSign/U64) would produce a WRONG empty-walk body
        // (`return true` / `SAME` / `0`) — such a type must IMPLEMENT those itself (`@override`).
        // But an IDENTITY derive (assign/copy → returns `Me`) is CORRECT as `return me` even with no
        // members, so it still auto-derives (signal = return type is the self type). A DELEGATION
        // derive (lt = `me.cmp(you) == ME_SMALL`) is likewise correct regardless of member count —
        // it calls the type's own cmp, native on a field-less scalar — so the rule does not gate it.
        if (memberCount == 0 && member.ReturnType is not ProtocolSelfTypeInfo)
        {
            return true;
        }

        // Uniform "already provided" check — NO type-category special rule: skip when the type
        // already resolves a CONCRETE (non-abstract) impl of this member, whether a hand-written
        // routine, an earlier hardcoded stub, or a native/wired op on an @llvm scalar (U64.cmp etc.).
        // A resolution to the ABSTRACT protocol member (OwnerType is a protocol) does NOT count —
        // that is the obligation this derive fulfils. Mirrors ComputeCapability's `direct` check.
        // For a BASE derive, skip when the type already resolves a CONCRETE (non-abstract) impl of
        // this member (a hand-written routine or a native/wired op on an @llvm scalar). A DERIVED
        // operator (lt/le/gt/ge) is NOT gated by this: LookupMemberRoutine resolves the abstract
        // Comparable.lt SUBSTITUTED to the implementer (owner rewritten to the type, so the
        // `OwnerType is protocol` test can't see it's the abstract obligation), which would wrongly
        // block the template-derived registration and re-open RF-S702. The derived operator is only
        // ever provided by this template path (DerivedOperatorPass no longer emits it), so register it.
        return _registry.LookupMemberRoutine(type: type, memberRoutineName: member.Name) is
            { OwnerType: not ProtocolTypeInfo };
    }

    /// <summary>
    /// Registers a single everywhere-derive stub for <paramref name="member"/> on <paramref name="type"/>
    /// when all eligibility gates pass. Builds the concrete parameter list by substituting
    /// <see cref="ProtocolSelfTypeInfo"/> slots with the owner type. Extracted from
    /// <see cref="RegisterEverywhereDeriveMembers"/> to reduce its cognitive complexity.
    /// </summary>
    private void RegisterEverywhereDeriveMember(TypeSymbol type, ProtocolMemberRoutineInfo member,
        int memberCount, bool everywhereObeys)
    {
        if (ShouldSkipEverywhereDeriveMember(type: type,
                member: member,
                memberCount: memberCount,
                everywhereObeys: everywhereObeys,
                isDerivedOperator: out _))
        {
            return;
        }

        // Build the stub from the protocol's declared signature, substituting the self type.
        var parameters = new List<ParameterInfo>();
        for (int i = 0; i < member.ParameterTypes.Count; i++)
        {
            TypeSymbol pt = member.ParameterTypes[index: i] is ProtocolSelfTypeInfo
                ? type
                : member.ParameterTypes[index: i];
            string pn = i < member.ParameterNames.Count
                ? member.ParameterNames[index: i]
                : $"arg{i}";
            parameters.Add(item: new ParameterInfo(name: pn, type: pt));
        }

        TypeSymbol? returnType = member.ReturnType is ProtocolSelfTypeInfo
            ? type
            : member.ReturnType;

        _registry.RegisterRoutine(routine: new RoutineInfo(name: member.Name)
        {
            Kind = RoutineKind.MemberRoutine,
            OwnerType = type,
            Parameters = parameters,
            ReturnType = returnType,
            IsFailable = member.IsFailable,
            DeclaredMutation = member.Mutation,
            MutationCategory = member.Mutation,
            Visibility = VisibilityModifier.Open,
            IsSynthesized = true
        });
    }

    /// <summary>
    /// Registers a single-parameter readonly wired routine if not already defined.
    /// </summary>
    private void MaybeRegisterWiredWithParam(TypeSymbol owner, string name, string paramName,
        TypeSymbol paramType, TypeSymbol returnType, List<RoutineInfo> existingMemberRoutines)
    {
        if (existingMemberRoutines.Any(predicate: m => m.Name == name))
        {
            return;
        }

        _registry.RegisterRoutine(routine: new RoutineInfo(name: name)
        {
            Kind = RoutineKind.MemberRoutine,
            OwnerType = owner,
            Parameters = [new ParameterInfo(name: paramName, type: paramType)],
            ReturnType = returnType,
            IsFailable = false,
            DeclaredMutation = MutationCategory.Readonly,
            MutationCategory = MutationCategory.Readonly,
            Visibility = VisibilityModifier.Open,
            IsSynthesized = true
        });
    }

    /// <summary>
    /// Registers the two auto-generated constructors for each non-<c>None</c> arm of a variant:
    /// <c>V.create(from: Arm) -> V</c> (box) and <c>Arm.create!(from: V) -> Arm</c> (failable extract).
    /// Each is overloaded by the <c>from:</c> parameter type, so an arm type shared across variants gets a
    /// distinct constructor per variant, and no arm-name/type-name collision (RF-S770) arises.
    /// </summary>
    private void RegisterVariantArmConstructors(VariantTypeInfo variant)
    {
        foreach (VariantMemberInfo arm in variant.Members)
        {
            if (arm.IsNone || arm.Type is null || arm.Type is ErrorTypeInfo)
            {
                continue;
            }

            TypeSymbol armType = arm.Type;

            // V.create(from: Arm) -> V
            bool ctorExists = _registry.GetMemberRoutinesForType(type: variant)
                                       .Any(predicate: m =>
                                            m is { IsCreator: true, Parameters.Count: 1 } &&
                                            m.Parameters[index: 0].Type?.FullName ==
                                            armType.FullName);
            if (!ctorExists)
            {
                _registry.RegisterRoutine(routine: new RoutineInfo(name: RoutineInfo.CreatorName)
                {
                    Kind = RoutineKind.Creator,
                    OwnerType = variant,
                    Parameters = [new ParameterInfo(name: "from", type: armType)],
                    ReturnType = variant,
                    IsFailable = false,
                    DeclaredMutation = MutationCategory.Readonly,
                    MutationCategory = MutationCategory.Readonly,
                    Visibility = VisibilityModifier.Open,
                    IsSynthesized = true
                });
            }

            // Arm.create!(from: V) -> Arm  (name "create" + IsFailable; a `.create!(…)` call resolves
            // against "create" and the `from: V` param type disambiguates from numeric conversions).
            bool extractExists = _registry.GetMemberRoutinesForType(type: armType)
                                          .Any(predicate: m =>
                                               m is
                                               {
                                                   IsCreator: true, Parameters.Count: 1,
                                                   IsFailable: true
                                               } && m.Parameters[index: 0].Type?.FullName ==
                                               variant.FullName);
            if (!extractExists)
            {
                _registry.RegisterRoutine(routine: new RoutineInfo(name: RoutineInfo.CreatorName)
                {
                    Kind = RoutineKind.Creator,
                    OwnerType = armType,
                    Parameters = [new ParameterInfo(name: "from", type: variant)],
                    ReturnType = armType,
                    IsFailable = true,
                    DeclaredMutation = MutationCategory.Readonly,
                    MutationCategory = MutationCategory.Readonly,
                    Visibility = VisibilityModifier.Open,
                    IsSynthesized = true
                });
            }
        }
    }

    private void MaybeRegisterWiredFailable(TypeSymbol owner, string name, TypeSymbol returnType,
        List<RoutineInfo> existingMemberRoutines, (string name, TypeSymbol type)? param = null,
        RoutineKind kind = RoutineKind.MemberRoutine)
    {
        if (existingMemberRoutines.Any(predicate: m => m.Name == name))
        {
            return;
        }

        _registry.RegisterRoutine(routine: new RoutineInfo(name: name)
        {
            Kind = kind,
            OwnerType = owner,
            Parameters = param.HasValue
                ? [new ParameterInfo(name: param.Value.name, type: param.Value.type)]
                : [],
            ReturnType = returnType,
            IsFailable = true,
            DeclaredMutation = MutationCategory.Readonly,
            MutationCategory = MutationCategory.Readonly,
            Visibility = VisibilityModifier.Open,
            IsSynthesized = true
        });
    }

    private bool ObeysProtocol(TypeSymbol type, string protocolName)
    {
        List<TypeSymbol>? implemented = type switch
        {
            ChoiceTypeInfo c => c.ImplementedProtocols,
            FlagsTypeInfo f => f.ImplementedProtocols,
            RecordTypeInfo r => r.ImplementedProtocols,
            EntityTypeInfo e => e.ImplementedProtocols,
            _ => null
        };
        if (implemented == null)
        {
            return false;
        }

        var seen = new HashSet<string>();
        return implemented.Any(predicate: p =>
            CheckProtocol(candidate: p, targetName: protocolName, seen: seen));
    }

    private bool CheckProtocol(TypeSymbol candidate, string targetName, HashSet<string> seen)
    {
        if (!seen.Add(item: candidate.Name))
        {
            return false;
        }

        if (candidate.Name == targetName)
        {
            return true;
        }

        // Resolve the latest version from the registry — ImplementedProtocols entries
        // can be stale (immutable type updates). The fully-populated parent list lives
        // on the registry's current ProtocolTypeInfo.
        TypeSymbol latest = _registry.LookupType(name: candidate.Name) ?? candidate;
        if (latest is ProtocolTypeInfo proto)
        {
            return proto.ParentProtocols.Any(predicate: parent =>
                CheckProtocol(candidate: parent, targetName: targetName, seen: seen));
        }

        return false;
    }
}
