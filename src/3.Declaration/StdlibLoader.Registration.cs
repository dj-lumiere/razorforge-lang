using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Compiler.Declaration;

public sealed partial class StdlibLoader
{
    /// <summary>Surface spelling of the creator (constructor) keyword in stdlib routine declarations,
    /// used to detect and normalize the old <c>routine T.create(...)</c> form.</summary>
    private const string SurfaceCreateKeyword = "create";

    private static void ResolveProtocolParents(TypeRegistry registry, Program program)
    {
        foreach (ISyntaxTreeNode node in program.Declarations)
        {
            if (node is ProtocolDeclaration { ParentProtocols.Count: > 0 } protocol)
            {
                ResolveProtocolParentsFor(registry: registry, protocol: protocol);
            }
        }
    }

    private static void ResolveProtocolParentsFor(TypeRegistry registry, ProtocolDeclaration protocol)
    {
        // Look up the registered protocol to get its FullName
        TypeInfo? registeredProto = registry.LookupType(name: protocol.Name);
        if (registeredProto is not ProtocolTypeInfo)
        {
            return;
        }

        var parentProtocols = new List<ProtocolTypeInfo>();
        foreach (TypeExpression parentExpr in protocol.ParentProtocols)
        {
            TypeInfo? parentType =
                ResolveSimpleType(registry: registry, typeExpr: parentExpr);
            if (parentType is ProtocolTypeInfo parentProto)
            {
                parentProtocols.Add(item: parentProto);
            }
        }

        if (parentProtocols.Count > 0)
        {
            registry.UpdateProtocolParents(protocolName: registeredProto.FullName,
                parentProtocols: parentProtocols);
        }
    }

    /// <summary>
    /// The realm ("RF"/"SF") stamped onto type-definition shells built during the current registration
    /// pass — set per-program from its source file extension (see <see cref="RealmOf"/>) before each
    /// shell-building pass loop, read by every <c>new …TypeInfo { … Realm = _registeringRealm }</c> below.
    /// A thread-static field avoids threading a realm parameter through the whole static registration API;
    /// resolved generic instances inherit it from their definition via CreateInstance propagation.
    /// </summary>
    [ThreadStatic] private static string? _registeringRealm;

    /// <summary>The realm a stdlib file belongs to: <c>"SF"</c> for a <c>.sf</c> source, else <c>"RF"</c>.</summary>
    private static string RealmOf(string filePath) =>
        filePath.EndsWith(value: ".sf", comparisonType: StringComparison.OrdinalIgnoreCase) ? "SF" : "RF";

    /// <summary>
    /// Sets the thread-static <see cref="_registeringRealm"/> from a static context, so instance-method
    /// callers do not directly write a static field (avoids instance-writes-static-field lint). Every
    /// realm stamp in the multi-pass registration loops routes through here.
    /// </summary>
    private static void StampRealm(string? realm) => _registeringRealm = realm;

    /// <summary>
    /// Registers type declarations (record, entity, choice, variant, protocol) from a program.
    /// This is pass 1b of module-based loading. Protocols may already be registered from pass 1a.
    /// </summary>
    /// <param name="registry">The type registry to register types into.</param>
    /// <param name="program">The parsed program AST.</param>
    /// <param name="moduleName">The module for the types (from declaration or directory-derived).</param>
    private static void RegisterProgramTypes(TypeRegistry registry, Program program,
        string moduleName)
    {
        foreach (ISyntaxTreeNode node in program.Declarations)
        {
            switch (node)
            {
                case RecordDeclaration record:
                    RegisterRecordType(registry: registry, record: record, moduleName: moduleName);
                    break;
                case EntityDeclaration entity:
                    RegisterEntityType(registry: registry, entity: entity, moduleName: moduleName);
                    break;
                case ChoiceDeclaration choice:
                    RegisterChoiceType(registry: registry, choice: choice, moduleName: moduleName);
                    break;
                case FlagsDeclaration flags:
                    RegisterFlagsType(registry: registry, flags: flags, moduleName: moduleName);
                    break;
                case VariantDeclaration variant:
                    RegisterVariantType(registry: registry,
                        variant: variant,
                        moduleName: moduleName);
                    break;
                case ProtocolDeclaration protocol:
                    RegisterProtocolType(registry: registry,
                        protocol: protocol,
                        moduleName: moduleName);
                    break;
                case CrashableDeclaration crashable:
                    RegisterCrashableType(registry: registry,
                        crashable: crashable,
                        moduleName: moduleName);
                    break;
            }
        }
    }

    /// <summary>
    /// Re-resolves member variables for types that had unresolvable forward references
    /// during initial registration. Called after all type shells are registered.
    /// </summary>
    private static void ResolveProgramMemberVariables(TypeRegistry registry, Program program)
    {
        // The deferred member-variable re-resolution must find THIS program's types by their
        // module-qualified name. A bare `LookupType(name)` depended on the cross-module short-name scan
        // (e.g. `Complex` → `Numerics.Complex`); with that scan gone the bare lookup misses, `existing`
        // is null, and the type's member variables never resolve — leaving fields like `Complex.real: Real`
        // untyped, so `me.real + you.real` reaches codegen with a `<error>` receiver.
        string? programModule = program.Declarations.OfType<ModuleDeclaration>().FirstOrDefault()?.Path;
        TypeInfo? LookupOwn(string typeName) =>
            (programModule != null ? registry.LookupType(name: $"{programModule}.{typeName}") : null)
            ?? registry.LookupType(name: typeName);

        foreach (ISyntaxTreeNode node in program.Declarations)
        {
            switch (node)
            {
                case EntityDeclaration entity:
                    ReResolveEntityMemberVariables(registry: registry, entity: entity,
                        existing: LookupOwn(entity.Name) as EntityTypeInfo);
                    break;
                case RecordDeclaration record:
                    ReResolveRecordMemberVariables(registry: registry, record: record,
                        existing: LookupOwn(record.Name) as RecordTypeInfo);
                    break;
                case VariantDeclaration variant:
                    ReResolveVariantMembers(registry: registry, variant: variant,
                        existing: LookupOwn(variant.Name) as VariantTypeInfo);
                    break;
                case CrashableDeclaration crashable:
                    ReResolveCrashableMemberVariables(registry: registry, crashable: crashable,
                        existing: LookupOwn(crashable.Name) as CrashableTypeInfo);
                    break;
            }
        }
    }

    private static void ReResolveEntityMemberVariables(TypeRegistry registry,
        EntityDeclaration entity, EntityTypeInfo? existing)
    {
        int expectedCount = entity.Members.Count(predicate: m =>
            m is VariableDeclaration { Type: not null });
        if (existing == null || existing.MemberVariables.Count >= expectedCount)
        {
            return;
        }

        List<MemberVariableInfo> members = ResolveMemberVariables(registry: registry,
            members: entity.Members,
            genericParams: entity.GenericParameters,
            owner: existing,
            moduleName: existing.Module);
        if (members.Count > existing.MemberVariables.Count)
        {
            existing.MemberVariables = members;
            registry.RefreshEntityResolutions(genericDef: existing);
        }
    }

    private static void ReResolveRecordMemberVariables(TypeRegistry registry,
        RecordDeclaration record, RecordTypeInfo? existing)
    {
        int expectedCount = record.Members.Count(predicate: m =>
            m is VariableDeclaration { Type: not null });
        if (existing == null || existing.MemberVariables.Count >= expectedCount)
        {
            return;
        }

        List<MemberVariableInfo> members = ResolveMemberVariables(registry: registry,
            members: record.Members,
            genericParams: record.GenericParameters,
            owner: existing,
            moduleName: existing.Module);
        if (members.Count > existing.MemberVariables.Count)
        {
            existing.MemberVariables = members;
        }
    }

    private static void ReResolveVariantMembers(TypeRegistry registry,
        VariantDeclaration variant, VariantTypeInfo? existing)
    {
        // Total declared arms (incl. None). If fewer resolved, some arm was a forward or
        // self reference (e.g. List[SerialValue]) unresolvable on the first pass — retry now.
        int expectedCount = variant.Members.Count;
        if (existing == null || existing.Members.Count >= expectedCount)
        {
            return;
        }

        List<VariantMemberInfo> reMembers = BuildVariantMembers(registry: registry,
            variant: variant, moduleName: existing.Module);
        if (reMembers.Count > existing.Members.Count)
        {
            existing.Members = reMembers;
        }
    }

    private static void ReResolveCrashableMemberVariables(TypeRegistry registry,
        CrashableDeclaration crashable, CrashableTypeInfo? existing)
    {
        int expectedCount = crashable.Members.Count(predicate: m =>
            m is VariableDeclaration { Type: not null });
        if (existing == null || existing.MemberVariables.Count >= expectedCount)
        {
            return;
        }

        List<MemberVariableInfo> members = ResolveMemberVariables(registry: registry,
            members: crashable.Members,
            genericParams: null,
            owner: existing,
            moduleName: existing.Module);
        if (members.Count > existing.MemberVariables.Count)
        {
            registry.UpdateCrashableMemberVariables(typeName: existing.FullName,
                memberVariables: members);
        }
    }

    /// <summary>
    /// Re-resolves protocol conformances for types whose protocol arguments contain
    /// forward-referenced types (e.g., EnumerateIterator[T] obeys Iterable[Tuple[S64, T]]
    /// where S64 wasn't registered during initial entity registration).
    /// Called after all type shells are registered.
    /// </summary>
    internal static void ResolveProgramProtocolConformances(TypeRegistry registry, Program program)
    {
        // Resolve type lookups MODULE-QUALIFIED. `LookupType(bareName)` resolves via the first-wins
        // short-name index, so with two modules each declaring `record Point` it would attach one
        // module's `obeys` to the OTHER module's type (cross-module protocol contamination →
        // spurious RF-S702). The program's own module scopes the lookup to its own declaration.
        // Scope the lookup to the CURRENTLY-REGISTERING realm (`_registeringRealm`, stamped per program by
        // the caller): with an RF `.rf` type and its SF `.sf` wrapper both bearing the same module-qualified
        // name, a realm-blind lookup would attach this program's `obeys` to the OTHER realm's shell.
        string realm = _registeringRealm ?? "RF";
        string? module = program.Declarations.OfType<ModuleDeclaration>().FirstOrDefault()?.Path;
        TypeInfo? LookupInModule(string name) =>
            (!string.IsNullOrEmpty(value: module)
                ? registry.LookupType(name: $"{module}.{name}", realm: realm)
                : null) ?? registry.LookupType(name: name, realm: realm);

        foreach (ISyntaxTreeNode node in program.Declarations)
        {
            switch (node)
            {
                case EntityDeclaration { Protocols.Count: > 0 } entity:
                    ReResolveEntityProtocolConformances(registry: registry, entity: entity,
                        existing: LookupInModule(name: entity.Name) as EntityTypeInfo);
                    break;
                case RecordDeclaration { Protocols.Count: > 0 } record:
                    ReResolveRecordProtocolConformances(registry: registry, record: record,
                        existing: LookupInModule(name: record.Name) as RecordTypeInfo);
                    break;
            }
        }
    }

    private static void ReResolveEntityProtocolConformances(TypeRegistry registry,
        EntityDeclaration entity, EntityTypeInfo? existing)
    {
        if (existing == null ||
            existing.ImplementedProtocols.Count >= entity.Protocols.Count)
        {
            return;
        }

        List<TypeInfo> protocols = ResolveProtocolList(registry: registry,
            protoExprs: entity.Protocols,
            genericParams: entity.GenericParameters);
        if (protocols.Count > existing.ImplementedProtocols.Count)
        {
            existing.ImplementedProtocols = protocols;
        }
        existing.ConditionalObeys ??= BuildConditionalObeys(protoExprs: entity.Protocols);
    }

    private static void ReResolveRecordProtocolConformances(TypeRegistry registry,
        RecordDeclaration record, RecordTypeInfo? existing)
    {
        if (existing == null ||
            existing.ImplementedProtocols.Count >= record.Protocols.Count)
        {
            return;
        }

        List<TypeInfo> protocols = ResolveProtocolList(registry: registry,
            protoExprs: record.Protocols,
            genericParams: record.GenericParameters);
        if (protocols.Count > existing.ImplementedProtocols.Count)
        {
            existing.ImplementedProtocols = protocols;
        }
        existing.ConditionalObeys ??= BuildConditionalObeys(protoExprs: record.Protocols);
    }

    /// <summary>
    /// Resolves a list of protocol type expressions into TypeInfo instances.
    /// </summary>
    private static List<TypeInfo> ResolveProtocolList(TypeRegistry registry,
        List<TypeExpression> protoExprs, List<string>? genericParams)
    {
        var result = new List<TypeInfo>();
        foreach (TypeExpression protoExpr in protoExprs)
        {
            TypeInfo? protoType = ResolveSimpleType(registry: registry,
                typeExpr: protoExpr,
                genericParams: genericParams);
            if (protoType != null)
            {
                result.Add(item: protoType);
            }
        }

        return result;
    }

    /// <summary>
    /// Resolves member variable types from a list of member declarations.
    /// </summary>
    private static List<MemberVariableInfo> ResolveMemberVariables(TypeRegistry registry,
        List<SyntaxTree.Declaration> members, List<string>? genericParams,
        TypeInfo? owner = null, string? moduleName = null)
    {
        var result = new List<MemberVariableInfo>();
        int index = 0;
        foreach (SyntaxTree.Declaration member in members)
        {
            if (member is VariableDeclaration { Type: not null } memberVariable)
            {
                TypeInfo? memberVariableType = ResolveSimpleType(registry: registry,
                    typeExpr: memberVariable.Type,
                    genericParams: genericParams,
                    moduleName: moduleName);
                if (memberVariableType != null)
                {
                    result.Add(
                        item: new MemberVariableInfo(name: memberVariable.Name,
                            type: memberVariableType)
                        {
                            Visibility = memberVariable.Visibility,
                            Index = index,
                            Owner = owner
                        });
                    index++;
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Registers routine declarations from a program.
    /// This is pass 2 of module-based loading - all types are already registered.
    /// </summary>
    /// <summary>
    /// BuilderQuery per-type routines whose stdlib decls return
    /// <c>List[Owned[FieldInfo|ProtocolInfo|RoutineInfo]]</c> or <c>Dict[Text, Data]</c>.
    /// Registering these as universal <c>T.x()</c> routines from BuilderQuery.rf forces GMP to
    /// monomorphize the heavy carrier closure (BTreeListNode/Owned/Array/ArrayIterator) for every
    /// type even when the user program never imports BuilderQuery.
    /// AutoWiredRegistrationPass re-registers these per-type when (and only when) the user actually
    /// imports BuilderQuery, so skipping the stdlib decls here is safe.
    /// </summary>
    private static readonly HashSet<string> BuilderQueryClosureCascadingRoutines =
        new(comparer: StringComparer.Ordinal)
        {
            "protocol_info",
            "routine_info",
            "member_variable_info"
        };

    private static bool ShouldSkipBuilderQueryRoutineDecl(RoutineDeclaration routine,
        string moduleName)
    {
        if (!moduleName.Equals(value: "BuilderQuery", comparisonType: StringComparison.Ordinal))
        {
            return false;
        }

        // Structured owner/member (parser-captured), never a re-split of the dotted Name string.
        string memberRoutine = routine.MemberRoutineName ?? routine.Name;
        if (BuilderQueryClosureCascadingRoutines.Contains(item: memberRoutine))
        {
            return true;
        }

        // Standalone BuilderQuery routines (build_mode/target_os/source_*/page_size/…) are provided
        // by the compiler: RegisterStandaloneRoutines/RegisterModuleRoutines register a single synthesized
        // RoutineInfo (module BuilderQuery) whose body WiredRoutinePass folds to a build-time literal,
        // and the source-location ones are folded at their call sites. The stdlib `@innate` decl is only
        // the surface signature — registering it too would create a SECOND, bodiless routine under the
        // same BuilderQuery.<name> identity, and codegen would emit a call to the undefined one. So the
        // stdlib standalone decl is skipped; the synthesized routine is the sole definition.
        return routine.MemberRoutineName is null
            && RuntimeContract.BuilderStandaloneRoutines.Contains(item: routine.Name);
    }

    private static void RegisterProgramRoutines(TypeRegistry registry, Program program,
        string moduleName)
    {
        foreach (ISyntaxTreeNode node in program.Declarations)
        {
            switch (node)
            {
                case RoutineDeclaration routine:
                    if (!ShouldSkipBuilderQueryRoutineDecl(routine: routine,
                            moduleName: moduleName))
                    {
                        RegisterRoutine(registry: registry, routine: routine, moduleName: moduleName);
                    }
                    break;
                case ExternalDeclaration external:
                    RegisterExternalDeclaration(registry: registry,
                        external: external,
                        moduleName: moduleName);
                    break;
                case ExternalBlockDeclaration block:
                    RegisterExternalBlockDeclarations(registry: registry, block: block,
                        moduleName: moduleName);
                    break;
                case CrashableDeclaration crashable:
                    RegisterCrashableRoutineMembers(registry: registry, crashable: crashable,
                        moduleName: moduleName);
                    break;
            }
        }
    }

    /// <summary>
    /// Registers every <c>external("C")</c> declaration contained in an external block.
    /// </summary>
    private static void RegisterExternalBlockDeclarations(TypeRegistry registry,
        ExternalBlockDeclaration block, string moduleName)
    {
        foreach (SyntaxTree.Declaration decl in block.Declarations)
        {
            if (decl is ExternalDeclaration ext)
            {
                RegisterExternalDeclaration(registry: registry,
                    external: ext,
                    moduleName: moduleName);
            }
        }
    }

    /// <summary>
    /// Registers a crashable type's routine members (e.g., crash_message synthesized from the
    /// message: directive), prefixing each with the type name so RegisterRoutine treats it as a
    /// member routine (e.g., "DivisionByZeroError.crash_message").
    /// </summary>
    private static void RegisterCrashableRoutineMembers(TypeRegistry registry,
        CrashableDeclaration crashable, string moduleName)
    {
        foreach (SyntaxTree.Declaration member in crashable.Members)
        {
            if (member is RoutineDeclaration memberRoutine)
            {
                // Prefix the memberRoutine name with the type name so RegisterRoutine
                // treats it as a member memberRoutine (e.g., "DivisionByZeroError.crash_message")
                var prefixed = memberRoutine with
                {
                    Name = $"{crashable.Name}.{memberRoutine.Name}"
                };
                RegisterRoutine(registry: registry,
                    routine: prefixed,
                    moduleName: moduleName);
            }
        }
    }

    /// <summary>
    /// Registers an external("C") declaration from stdlib (e.g., NativeDeclarations.rf).
    /// </summary>
    private static void RegisterExternalDeclaration(TypeRegistry registry,
        ExternalDeclaration external, string moduleName)
    {
        // Build generic context for type resolution (e.g., T, To, From)
        List<string>? genericCtx = external.GenericParameters is { Count: > 0 }
            ? external.GenericParameters
            : null;

        // Resolve parameter types
        var parameters = new List<ParameterInfo>();
        foreach (Parameter param in external.Parameters)
        {
            TypeInfo? paramType = ResolveSimpleType(registry: registry,
                typeExpr: param.Type,
                genericParams: genericCtx,
                moduleName: moduleName);
            parameters.Add(
                item: new ParameterInfo(name: param.Name,
                    type: paramType ?? ErrorTypeInfo.Instance)
                {
                    DefaultValue = param.DefaultValue, IsVariadicParam = param.IsVariadic
                });
        }

        // Resolve return type
        TypeInfo? returnType = external.ReturnType != null
            ? ResolveSimpleType(registry: registry,
                typeExpr: external.ReturnType,
                genericParams: genericCtx,
                moduleName: moduleName)
            : null;

        (string? linkLibrary, string? linkSymbol) =
            ExtractExternalLinkBinding(annotations: external.Annotations);

        var routineInfo = new RoutineInfo(name: external.Name)
        {
            // Foreign-ness is now carried by RoutineInfo.Realm (derived from CallingConvention).
            IsFailable = external.IsFailable,
            CallingConvention = external.CallingConvention ?? "C",
            LinkLibrary = linkLibrary,
            LinkSymbol = linkSymbol,
            IsVariadic = external.IsVariadic,
            Parameters = parameters,
            ReturnType = returnType,
            Module = moduleName,
            ModulePath = moduleName?.Split('/').ToList(),
            Location = external.Location,
            IsDangerous = external.IsDangerous,
            GenericParameters = external.GenericParameters,
            Annotations = external.Annotations ?? []
        };

        try
        {
            registry.RegisterRoutine(routine: routineInfo);
        }
        catch
        {
            // Ignore duplicate routine registration
        }
    }

    /// <summary>
    /// First <c>@link(...)</c> binding (library, symbol-override) on a foreign declaration, or (null, null).
    /// </summary>
    private static (string? Library, string? Symbol) ExtractExternalLinkBinding(List<string>? annotations)
    {
        if (annotations == null)
        {
            return (null, null);
        }

        foreach (string ann in annotations)
        {
            (string? lib, string? symbol) = TypeModel.Symbols.LinkAnnotation.Parse(annotation: ann);
            if (lib != null || symbol != null)
            {
                return (lib, symbol);
            }
        }

        return (null, null);
    }

    /// <summary>
    /// Registers preset (build-time constant) declarations from a program.
    /// Presets are module-level constants accessible across files within the same module.
    /// </summary>
    private static void RegisterProgramPresets(TypeRegistry registry, Program program,
        string moduleName)
    {
        foreach (ISyntaxTreeNode node in program.Declarations)
        {
            if (node is PresetDeclaration preset)
            {
                // Pass the module context so the preset's type resolves via its OWN module prefix
                // (e.g. `Q32_IDENTITY: Q32` in `module Math3D` → `Math3D.Q32`) instead of a bare lookup
                // that depended on the cross-module short-name scan. A null type would drop the preset
                // entirely (a bare cross-module reference then fails as UnknownIdentifier).
                TypeInfo? presetType =
                    ResolveSimpleType(registry: registry, typeExpr: preset.Type, moduleName: moduleName);
                if (presetType != null)
                {
                    SeedPresetValueMetadata(value: preset.Value, presetType: presetType);
                    registry.RegisterPreset(name: preset.Name,
                        type: presetType,
                        module: moduleName,
                        value: preset.Value,
                        isSecret: preset.IsSecret);
                }
            }
        }
    }

    private static void SeedPresetValueMetadata(Expression value, TypeInfo presetType)
    {
        value.ResolvedType ??= presetType;

        if (value is not CallExpression call ||
            call.Callee is not IdentifierExpression identifier ||
            call.LoweringKind != CallLoweringKind.Unknown)
        {
            return;
        }

        bool matchesPresetType = identifier.Name == presetType.Name ||
                                 identifier.Name == presetType.FullName ||
                                 presetType.FullName.EndsWith(value: "." + identifier.Name,
                                     comparisonType: StringComparison.Ordinal);
        if (!matchesPresetType)
        {
            return;
        }

        call.ResolvedType ??= presetType;
        call.ConstructedType ??= presetType;

        call.LoweringKind = presetType switch
        {
            RecordTypeInfo { BackendType: not null } => CallLoweringKind.TypeConstructor,
            _ => call.LoweringKind
        };
    }

    /// <summary>
    /// Resolves the owner type for a stdlib routine from its rendered receiver. For a member routine
    /// (<c>List[T].add_last</c>, <c>S32.add</c>) this is the receiver type; for a bare constructor
    /// (<c>routine List(...)</c>) it is the constructed type and <paramref name="memberRoutineName"/>
    /// is rewritten to "create". For a generic-specialized receiver (<c>List[Agent[V]]</c>) the owner
    /// stays the generic def and <paramref name="meTypeName"/> carries the receiver text so `me` can be
    /// typed as the specialization. Returns null when the receiver is a bare name matching no type and
    /// no constructor (a plain free routine).
    /// </summary>
    private static TypeInfo? ResolveRoutineOwner(TypeRegistry registry, RoutineDeclaration routine,
        string routineName, string moduleName, ref string memberRoutineName, out string? meTypeName)
    {
        meTypeName = null;

        if (routine.RenderedReceiver is { } typeName)
        {
            int bracketIndex = typeName.IndexOf(value: '[');
            return bracketIndex > 0
                ? ResolveBracketedReceiverOwner(registry: registry, routine: routine,
                    typeName: typeName, bracketIndex: bracketIndex, moduleName: moduleName,
                    memberRoutineName: memberRoutineName, meTypeName: out meTypeName)
                : ResolveBareReceiverOwner(registry: registry, typeName: typeName,
                    moduleName: moduleName);
        }

        // No dot: a top-level free function, OR a CONSTRUCTOR `routine T(...)` /
        // `routine T[params](...)` (renamed from `routine T.create(...)`). Detect the
        // constructor by matching the bare name against a known type and route it to the
        // reserved creator name "create" with that type as owner — mirroring the old
        // `T.create` registration so call-site construction resolves the creator. The
        // trailing `!` (failable) is carried structurally on routine.IsFailable.
        string bareName = TypeInfo.StripTypeArgs(name: routineName);
        // Own-module FIRST: a constructor `routine List(...)` in `module Suflae` owns `Suflae.List`,
        // NOT the first-registered context-free `List` (Core.List, loaded earlier). Resolving bare
        // first bound the Suflae overlay's `List()` creator to Core.List → same RegistryKey as
        // Core's own `List()` → a spurious divergent-duplicate-constructor error (RF-S406). This
        // mirrors LookupTypeWithImports's own-module-shadows rule for the stdlib registration path.
        // Own-REALM-first: SF-realm `Core.List`'s `List()` constructor must own the SF-realm List, not
        // the RazorForge-realm `Core.List` (same bare key, both `module Core`) — else both `List()`
        // creators share one RegistryKey and trip the divergent-duplicate-constructor check (RF-S406).
        TypeInfo? ctorOwner = registry.LookupType(name: $"{moduleName}.{bareName}", realm: _registeringRealm ?? "RF") ??
                              registry.LookupType(name: $"{moduleName}.{bareName}") ??
                              registry.LookupType(name: bareName);
        if (ctorOwner != null)
        {
            // A constructor carries NO member name — identity is RoutineKind.Creator, assigned below.
            memberRoutineName = RoutineInfo.CreatorName;
            return ctorOwner;
        }

        return null;
    }

    /// <summary>
    /// Resolves the owner for a bracketed receiver (<c>List[T]</c>, <c>List[Agent[V]]</c>,
    /// <c>List[Byte]</c>): a generic definition when the bracket args are the base's own params, a
    /// generic specialization (owner = def, receiver text captured in <paramref name="meTypeName"/>)
    /// when the brackets reference a routine generic param, else a concrete resolution.
    /// </summary>
    private static TypeInfo? ResolveBracketedReceiverOwner(TypeRegistry registry,
        RoutineDeclaration routine, string typeName, int bracketIndex, string moduleName,
        string memberRoutineName, out string? meTypeName)
    {
        meTypeName = null;

        // Check if the bracket content is concrete types (e.g., List[Byte])
        // vs generic params (e.g., List[T], Dict[K, V])
        string bracketContent = typeName[(bracketIndex + 1)..]
           .TrimEnd(trimChar: ']');
        string baseName = typeName[..bracketIndex];
        // Own-module FIRST: `routine List[T].add_last` in `module Suflae` owns `Suflae.List`,
        // not the earlier-registered context-free `Core.List`. Bare-first bound the Suflae
        // overlay's List memberRoutines to Core.List, so dispatch on `Roamed[Suflae.List]` found no
        // such memberRoutine (RF-S458). Falls back to bare for a Core type used from another module.
        // Own-REALM-first (like the non-bracketed owner path): an SF-realm `Core.List[T]` member
        // owns the SF-realm List, not the RazorForge-realm one that shares the bare key.
        TypeInfo? baseDef = registry.LookupType(name: $"{moduleName}.{baseName}", realm: _registeringRealm ?? "RF") ??
                            registry.LookupType(name: $"{moduleName}.{baseName}") ??
                            registry.LookupType(name: baseName);

        // If the base is a generic definition, check if bracket args are its own params
        bool isGenericDef = false;
        if (baseDef?.GenericParameters != null)
        {
            var args = bracketContent.Split(separator: ',')
                                     .Select(selector: a => a.Trim())
                                     .ToList();
            isGenericDef =
                args.All(predicate: a => baseDef.GenericParameters.Contains(value: a));
        }

        if (isGenericDef)
        {
            // Generic definition: List[T] -> owner is List
            return baseDef;
        }

        // Specialized receiver. Distinguish a GENERIC specialization (List[Agent[V]] —
        // brackets reference a routine generic param) from a fully-CONCRETE one (List[Byte]).
        bool hasGenericParamInReceiver = routine.GenericParameters?.Any(predicate: gp =>
            registry.LookupType(name: gp) is null &&
            registry.LookupType(name: $"{moduleName}.{gp}") is null &&
            System.Text.RegularExpressions.Regex.IsMatch(
                input: bracketContent,
                pattern: $@"\b{System.Text.RegularExpressions.Regex.Escape(str: gp)}\b")) ?? false;
        if (hasGenericParamInReceiver)
        {
            // GENERIC specialization (e.g. List[Agent[V]]): register under the generic def
            // (so call-site lookup on List[Agent[S64]] finds it), and remember the receiver
            // text so `me` is typed as the specialized receiver (MeType) below — making
            // member access like `me[i]` yield Agent[V] instead of List's raw element.
            meTypeName = memberRoutineName == RoutineInfo.CreatorName ? null : typeName;
            return baseDef;
        }

        // Concrete specialization (List[Byte]) -> owner is the concrete resolution.
        return registry.LookupType(name: typeName) ?? baseDef;
    }

    /// <summary>
    /// Resolves the owner for a bare (non-bracketed) receiver (<c>S32.add</c>, <c>T.view</c>):
    /// own-module + own-realm first, then bare; a receiver naming no registered type is treated as a
    /// generic type parameter.
    /// </summary>
    private static TypeInfo ResolveBareReceiverOwner(TypeRegistry registry, string typeName,
        string moduleName)
    {
        // Own-module FIRST (mirrors the constructor path + LookupTypeWithImports): a member
        // `routine List[T].add_last` in `module Suflae` owns `Suflae.List`, not the earlier-
        // registered context-free `Core.List`. Falls back to the bare context-free type for a
        // Core type referenced from another module (e.g. `Collections` memberRoutines on `Core.List`).
        // Own-REALM-first too: SF-realm `Core.List`'s members must own the SF-realm List, not the
        // RazorForge-realm `Core.List` that shares the bare key (both are `module Core`).
        TypeInfo? ownerType = registry.LookupType(name: $"{moduleName}.{typeName}", realm: _registeringRealm ?? "RF") ??
                    registry.LookupType(name: $"{moduleName}.{typeName}") ??
                    registry.LookupType(name: typeName);

        // If type not found, treat as a generic type parameter (e.g., T in "routine T.view()")
        return ownerType ?? new GenericParameterTypeInfo(name: typeName);
    }

    /// <summary>
    /// Registers a routine from stdlib (including type memberRoutines like S32.add).
    /// </summary>
    private static void RegisterRoutine(TypeRegistry registry, RoutineDeclaration routine,
        string moduleName)
    {
        // Desugar homogeneous variadic params (`nums...: T`) into a const-generic `Array[T, __VarargN]`
        // before generic-context collection reads routine.GenericParameters. Idempotent — safe to also
        // run in ResolveRoutineSignatures on the same node.
        VariadicParamDesugar.Apply(routine: routine);

        // Owner/member come from the parser-captured structured fields (the ONE canonical split);
        // `typeName` below is the RENDERED receiver ("S32", "List[Agent[V]]"), whose type-args are then
        // decoded for the generic-def-vs-specialization decision.
        string routineName = routine.Name;
        string memberRoutineName = routine.MemberRoutineName ?? routineName;
        // meTypeName carries the receiver text for a GENERIC specialization (e.g. "List[Agent[V]]");
        // resolved into MeType once the generic context is built, so `me` is typed as the specialized
        // receiver.
        TypeInfo? ownerType = ResolveRoutineOwner(registry: registry, routine: routine,
            routineName: routineName, moduleName: moduleName,
            memberRoutineName: ref memberRoutineName, meTypeName: out string? meTypeName);

        // `needs T is TypeName` declares T as a generic type parameter (equivalent to `[T]`, just a
        // different surface form), handled in the SA/registration layer — NOT a parser rewrite. Fold the
        // declared names into the AST decl's GenericParameters, the single list every downstream reader
        // (registration below, signature resolution, call-site inference, monomorphization) consults, so
        // `T` behaves exactly like a bracket param. Mutates the shared decl; idempotent.
        if (routine.GenericConstraints is { } typeNameDecls)
        {
            foreach (SyntaxTree.GenericConstraintDeclaration gc in typeNameDecls)
            {
                if (gc.ConstraintType != SyntaxTree.ConstraintKind.AnyType) continue;
                routine.GenericParameters ??= [];
                if (!routine.GenericParameters.Contains(item: gc.ParameterName))
                    routine.GenericParameters.Add(item: gc.ParameterName);
            }
        }

        List<string>? ctx = BuildRoutineGenericContext(registry: registry, routine: routine,
            ownerType: ownerType, moduleName: moduleName);

        List<ParameterInfo> parameters = ResolveRoutineParameters(registry: registry,
            routine: routine, ctx: ctx, moduleName: moduleName);

        TypeInfo? returnType = ResolveRoutineReturnType(registry: registry, routine: routine,
            ownerType: ownerType, ctx: ctx, moduleName: moduleName);

        // Resolve the specialized receiver (e.g. List[Agent[V]]) with the generic context now in
        // scope, so `me` is typed as the specialized receiver. OwnerType stays the generic def.
        TypeInfo? meType = ResolveSpecializedMeType(registry: registry, routine: routine,
            meTypeName: meTypeName, ownerType: ownerType, ctx: ctx, moduleName: moduleName);

        // RoutineKind assigned HERE, at declaration/registration — codegen must NOT re-derive it (the old
        // bug: this path left Kind at the default FreeRoutine, so a constructor's DEFINE wrongly gained an
        // implicit `me` param the static construction call omits → arg shift → NULL-write AV). The former
        // orthogonal `StorageClass.Common` axis is folded in as RoutineKind.CommonRoutine.
        // A constructor is spelled either as the bare `routine T(...)` (ResolveRoutineOwner cleared the
        // member name to RoutineInfo.CreatorName) OR the member form `routine T.create(...)` (surface
        // member name "create"). Both are the reserved Creator kind with NO internal name — normalize the
        // surface "create" token away here so nothing downstream keys off it.
        if (ownerType != null && memberRoutineName == SurfaceCreateKeyword)
            memberRoutineName = RoutineInfo.CreatorName;

        RoutineKind routineKind;
        if (memberRoutineName == RoutineInfo.CreatorName && ownerType != null)
            routineKind = RoutineKind.Creator;
        else if (routine.IsCommon)
            routineKind = RoutineKind.CommonRoutine;
        else if (ownerType != null)
            routineKind = RoutineKind.MemberRoutine;
        else
            routineKind = RoutineKind.FreeRoutine;

        // Use just the memberRoutine name (not "S32.add", just "add")
        var routineInfo = new RoutineInfo(name: memberRoutineName)
        {
            Kind = routineKind,
            OwnerType = ownerType,
            MeType = meType,
            Parameters = parameters,
            ReturnType = returnType,
            Module = moduleName,
            ModulePath = moduleName?.Split('/').ToList(),
            Location = routine.Location,
            Documentation = routine.Documentation,
            IsFailable = routine.IsFailable,
            IsWiredMemberRoutine = routine.IsWiredMemberRoutine,
            IsVariadic = routine.Parameters.Any(predicate: p => p.IsVariadic),
            GenericParameters = routine.GenericParameters,
            GenericConstraints = routine.GenericConstraints,
            AsyncStatus = routine.Async,
            Annotations = routine.Annotations,
            // StdlibLoader is a PARALLEL routine-registration path to SignatureResolver; it derives the
            // mutation category from @readonly/@reshaping through the SAME shared helper so the two paths
            // stay in lockstep. (Omitting it silently left every stdlib member routine at the RoutineInfo
            // default — e.g. a plainly-@readonly `List.count` looked Reshaping and tripped the RF-S625
            // iteration ban.)
            MutationCategory = Compiler.Verification.Enums.MutationCategoryExtensions.FromAnnotations(annotations: routine.Annotations),
            DeclaredMutation = Compiler.Verification.Enums.MutationCategoryExtensions.FromAnnotations(annotations: routine.Annotations),
            IsDangerous = routine.IsDangerous
        };

        // Opt-in derive templates (capability-gated) must not register as live universals — skip them.
        if (IsOptInDeriveTemplateToSkip(registry: registry, routine: routine, ownerType: ownerType,
                memberRoutineName: memberRoutineName))
        {
            return;
        }

        // Pin the decl → info binding (see RoutineDeclaration.ResolvedInfo) so codegen reads it
        // directly rather than re-deriving the routine by module-blind name lookup.
        routine.ResolvedInfo = routineInfo;

        // Constructor divergent-duplicate guard: hash the body so RegisterRoutine can distinguish a
        // benign identical cross-file duplicate creator from a divergent one (see
        // TypeRegistry.DivergentDuplicateCreators).
        if (routineInfo.IsCreator)
            routineInfo.BodyHash = TypeRegistry.ComputeCreatorBodyHash(body: routine.Body);

        try
        {
            registry.RegisterRoutine(routine: routineInfo);
        }
        catch
        {
            // Ignore duplicate routine registration
        }
    }

    /// <summary>
    /// Builds the generic-parameter context list for a stdlib routine registration. Includes any
    /// generic-parameter owner (T in "routine T.view()"), the owner type's own generic params, and
    /// the routine's declared generic params — filtering out receiver-leaf names that resolve to real
    /// registered types (concrete args like "Text" in "Iterable[Text]" must not shadow Core.Text).
    /// Returns null when the context is empty (no generic params in scope).
    /// </summary>
    private static List<string>? BuildRoutineGenericContext(TypeRegistry registry,
        RoutineDeclaration routine, TypeInfo? ownerType, string moduleName)
    {
        var genericContext = new List<string>();
        if (ownerType is GenericParameterTypeInfo genParam)
            genericContext.Add(item: genParam.Name);
        if (ownerType?.GenericParameters != null)
            genericContext.AddRange(collection: ownerType.GenericParameters);
        if (routine.GenericParameters != null)
        {
            // Filter out names that resolve to real registered types — but ONLY for RECEIVER-derived
            // leaves. The parser collects bracket contents from owner receivers like `Iterable[Text]`
            // and stuffs them into routine.GenericParameters; a concrete arg (Text) there must not
            // shadow the real type. A routine's OWN method-generic param (the `U` in
            // `Iterable[T].accumulate[U]`) is an EXPLICIT declaration — never dropped just because a
            // user type shares its name; its identity is its slot, not the label.
            HashSet<string> receiverLeaves = CollectReceiverLeafParamNames(routine.ReceiverType);
            foreach (string gp in routine.GenericParameters)
            {
                bool isReceiverBinding = receiverLeaves.Contains(item: gp)
                    && (registry.LookupType(name: gp) is not null
                        || registry.LookupType(name: $"{moduleName}.{gp}") is not null);
                if (!isReceiverBinding)
                    genericContext.Add(item: gp);
            }
        }
        return genericContext.Count > 0 ? genericContext : null;
    }

    /// <summary>
    /// Resolves each parameter's type for a stdlib routine using the supplied generic context.
    /// Variadic params are desugared to <c>Array[T, __VarargN]</c> upstream; no extra List wrapping here.
    /// Falls back to <see cref="ErrorTypeInfo.Instance"/> when a param type cannot be resolved.
    /// </summary>
    private static List<ParameterInfo> ResolveRoutineParameters(TypeRegistry registry,
        RoutineDeclaration routine, List<string>? ctx, string moduleName)
    {
        var parameters = new List<ParameterInfo>();
        foreach (Parameter param in routine.Parameters)
        {
            TypeInfo? paramType = ResolveSimpleType(registry: registry,
                typeExpr: param.Type, genericParams: ctx, moduleName: moduleName);
            parameters.Add(item: new ParameterInfo(name: param.Name,
                type: paramType ?? ErrorTypeInfo.Instance)
            {
                DefaultValue = param.DefaultValue, IsVariadicParam = param.IsVariadic
            });
        }
        return parameters;
    }

    /// <summary>
    /// Resolves the declared return type for a stdlib routine. A bare "Me" return on a concrete
    /// (non-protocol) member routine is rewritten to the owner type itself (applied to its own generic
    /// params for a generic def), because <see cref="ResolveSimpleType"/> has no owner context and would
    /// yield <see cref="ProtocolSelfTypeInfo"/> — which leaks to codegen as an unknown type category.
    /// </summary>
    private static TypeInfo? ResolveRoutineReturnType(TypeRegistry registry, RoutineDeclaration routine,
        TypeInfo? ownerType, List<string>? ctx, string moduleName)
    {
        TypeInfo? returnType = routine.ReturnType != null
            ? ResolveSimpleType(registry: registry, typeExpr: routine.ReturnType,
                genericParams: ctx, moduleName: moduleName)
            : null;
        if (routine.ReturnType is { Name: "Me", GenericArguments: not { Count: > 0 } }
            && ownerType != null && ownerType is not ProtocolTypeInfo)
        {
            returnType = ownerType is { IsGenericDefinition: true, GenericParameters: { Count: > 0 } ownerParams }
                ? registry.GetOrCreateResolution(genericDef: ownerType,
                    typeArguments: ownerParams.Select(selector: p => (TypeInfo)new GenericParameterTypeInfo(name: p)).ToList())
                : ownerType;
        }
        return returnType;
    }

    /// <summary>
    /// Resolves the specialized receiver (e.g. <c>List[Agent[V]]</c>) into a MeType with the generic
    /// context now in scope, so <c>me</c> is typed as the specialization while OwnerType stays the
    /// generic def. Returns null when there is no specialized receiver text or it fails to resolve.
    /// </summary>
    private static TypeInfo? ResolveSpecializedMeType(TypeRegistry registry, RoutineDeclaration routine,
        string? meTypeName, TypeInfo? ownerType, List<string>? ctx, string moduleName)
    {
        if (meTypeName == null)
        {
            return null;
        }

        TypeExpression? recvExpr =
            Compiler.Verification.SemanticVerifier.ParseTypeExpressionString(
                text: meTypeName, location: routine.Location);
        if (recvExpr == null)
        {
            return null;
        }

        TypeInfo? resolvedRecv = ResolveSimpleType(registry: registry,
            typeExpr: recvExpr, genericParams: ctx, moduleName: moduleName);
        if (resolvedRecv == null || resolvedRecv is ErrorTypeInfo)
        {
            return null;
        }

        // `ResolveSimpleType` is realm-blind and yields the ambient-realm receiver; keep `me`
        // in the OWNER's realm so an SF-realm `Core.List` method's `me` isn't the RF-realm List
        // (which lacks the SF wrapper's `inner` field → spurious RF-S450).
        return ownerType != null && resolvedRecv.Realm != ownerType.Realm
            ? (registry.ReResolveInRealm(type: resolvedRecv, realm: ownerType.Realm) ?? resolvedRecv)
            : resolvedRecv;
    }

    /// <summary>
    /// A derive template's UNIVERSAL-vs-OPT-IN status is read straight off its stdlib constraints —
    /// NOT a C# name list. A derive is OPT-IN (must NOT become a live universal, else it is force-
    /// instantiated for a type whose fields lack the capability → "declared but never defined" codegen
    /// crash) IFF it carries a CAPABILITY gate: <c>needs T is P everywhere</c> (∀-structural conformance)
    /// or <c>needs T obeys P</c>. A derive with NO gate (represent/diagnose/serialize/destroy/copy/store),
    /// or only a KIND gate (<c>needs T is VariantType</c> — that just selects WHICH override body wins in
    /// GetDeriveTemplate, not eligibility), is UNIVERSAL: it applies to every type. The per-type body
    /// always comes from the derive-template store via WiredRoutinePass.CloneUniversalDeriveBody, so a
    /// universal registration only supplies the resolvable signature — the kind-specialized override
    /// (e.g. <c>is RoutineType</c>) still supplies the body.
    /// Marks the memberRoutine opt-in as a side effect and returns true when this routine must be
    /// skipped (not registered).
    /// </summary>
    private static bool IsOptInDeriveTemplateToSkip(TypeRegistry registry, RoutineDeclaration routine,
        TypeInfo? ownerType, string memberRoutineName)
    {
        bool isDeriveTemplate = ownerType is GenericParameterTypeInfo
            && (routine.Annotations.Contains(item: "overridable")
                || routine.Annotations.Contains(item: "override"));
        bool hasCapabilityGate = (routine.GenericConstraints ?? []).Any(predicate: c =>
            c.ConstraintType is ConstraintKind.Everywhere or ConstraintKind.Obeys);
        if (isDeriveTemplate && hasCapabilityGate)
            registry.MarkOptInDeriveMemberRoutine(memberRoutine: memberRoutineName);
        // Opt-in status is per memberRoutine, not per template: once `copy`'s capability-gated base
        // (`needs Copyable everywhere`) marks `copy` opt-in, its KIND-gated variant override
        // (`needs T is VariantType`) must ALSO stay opt-in — else the override, being a bare-`T`-owner
        // routine, lands in `_universalMemberRoutines` and makes `copy` resolve for EVERY type (leaking `-> Me`/
        // ProtocolSelf, bypassing the Copyable gate). The `@overridable` base precedes its `@override`s in
        // DeriveText, so the memberRoutine is already marked when the override is seen. A truly universal derive
        // (represent/diagnose/serialize/destroy — never capability-gated) is never marked, so its kind
        // overrides register normally.
        return isDeriveTemplate && (hasCapabilityGate || registry.IsOptInDeriveMemberRoutine(memberRoutine: memberRoutineName));
    }

    /// <summary>
    /// The bare leaf identifiers of a member routine's RECEIVER type — the parameter names that came
    /// from the receiver's brackets (<c>T</c> in <c>Iterable[T]</c>, <c>K</c>/<c>V</c> in
    /// <c>List[DictEntry[K, V]]</c>). These may bind a CONCRETE type (<c>Text</c> in
    /// <c>Iterable[Text].join</c>); a method-generic like <c>U</c> in <c>Iterable[T].accumulate[U]</c>
    /// is NOT among them. Mirrors <c>SignatureResolver.CollectReceiverLeafParamNames</c>.
    /// </summary>
    private static HashSet<string> CollectReceiverLeafParamNames(TypeExpression? receiver)
    {
        var names = new HashSet<string>(comparer: System.StringComparer.Ordinal);
        if (receiver?.GenericArguments is { Count: > 0 } args)
        {
            foreach (TypeExpression arg in args)
            {
                CollectReceiverLeaves(type: arg, into: names);
            }
        }

        return names;
    }

    private static void CollectReceiverLeaves(TypeExpression type, HashSet<string> into)
    {
        if (type.GenericArguments is { Count: > 0 } args)
        {
            foreach (TypeExpression arg in args)
            {
                CollectReceiverLeaves(type: arg, into: into);
            }

            return;
        }

        if (type.Name.Contains(value: '.')) return;
        into.Add(item: type.Name);
    }

    /// <summary>
    /// Detects whether a stdlib record is an entity-type specialization of a constrained generic
    /// (e.g. <c>record Maybe[T] needs T is EntityType</c>) — which needs separate layout registration —
    /// versus a transparent pointer wrapper or an all-ptr-wrapper record that merely carries the
    /// <c>needs T is EntityType</c> contract but has a single fixed LLVM layout for all type arguments.
    /// </summary>
    private static bool IsEntityTypeSpecialization(RecordDeclaration record)
    {
        // In stdlib .rf files, `needs T is EntityType` is parsed as a ConstGeneric constraint
        // with ConstraintTypes[0].Name == "EntityType". These create a second layout specialization
        // (e.g. Maybe[Text] uses { Hijacked[T] } instead of { Bool, T }) and must be stored
        // separately so GetOrCreateResolution can select the right definition.
        string? entityConstraintParam = record.GenericConstraints?
            .Where(predicate: c =>
                c is { ConstraintType: ConstraintKind.ConstGeneric, ConstraintTypes: [{ Name: "EntityType" }] })
            .Select(selector: c => c.ParameterName)
            .FirstOrDefault();
        bool isEntitySpecialization = entityConstraintParam != null;

        // Transparent pointer wrappers (e.g. T) carry `needs T is EntityType` as a
        // contract annotation but have a single fixed LLVM layout (ptr) for all type arguments.
        // They are wrapper types — NOT entity specializations — so register them normally.
        if (isEntitySpecialization &&
            ExtractLlvmAnnotation(annotations: record.Annotations) == "ptr" &&
            !record.Members.Any(predicate: m => m is VariableDeclaration { Type: not null }))
        {
            isEntitySpecialization = false;
        }

        // Records whose member variables are all known @llvm("ptr") wrapper types (e.g.
        // Retained[T] with two Hijacked fields) have a fixed struct layout regardless of T.
        // They are NOT entity specializations — register them normally.
        if (isEntitySpecialization &&
            record.Members.Any(predicate: m => m is VariableDeclaration { Type: not null }))
        {
            bool allMembersPtrWrapper = record.Members
                .OfType<VariableDeclaration>()
                .Where(predicate: m => m.Type != null)
                .All(predicate: m =>
                {
                    // TypeExpression.Name is structurally bare — type args live in GenericArguments —
                    // so no bracket-strip is needed.
                    string baseName = m.Type!.Name;
                    return baseName is RuntimeContract.Hijacked or RuntimeContract.Viewing or RuntimeContract.Modifying
                        or RuntimeContract.Retained or RuntimeContract.Tracked or RuntimeContract.Guarded or RuntimeContract.Witnessed;
                });
            if (allMembersPtrWrapper)
            {
                isEntitySpecialization = false;
            }
        }

        return isEntitySpecialization;
    }

    /// <summary>
    /// Resolves a stdlib type's member variables and (decl-position) <c>expand m in allmemvarof(T)</c>
    /// column templates in one pass. The stdlib registration path does NOT run
    /// TypeBodyResolver.ResolveRecordBody (user-program only), so expand templates are resolved here —
    /// otherwise a stdlib SoA type (SplitList) never gets its ExpandTemplates and ExpandSoAColumns
    /// materializes no columns. Resolved templates are appended to <paramref name="expandTemplates"/>.
    /// </summary>
    private static List<MemberVariableInfo> BuildStdlibMemberVariables(TypeRegistry registry,
        List<SyntaxTree.Declaration> members, List<string>? genericParams, string moduleName,
        List<MemberExpandTemplateInfo> expandTemplates)
    {
        var memberVariables = new List<MemberVariableInfo>();
        foreach (SyntaxTree.Declaration member in members)
        {
            if (member is ExpandMemberDeclaration expandDecl)
            {
                foreach (ExpandMemberTemplate template in expandDecl.Templates)
                {
                    TypeInfo? columnType = ResolveSimpleType(registry: registry,
                        typeExpr: template.Type,
                        genericParams: genericParams,
                        moduleName: moduleName);
                    if (columnType != null)
                    {
                        expandTemplates.Add(item: new MemberExpandTemplateInfo(
                            namePrefix: template.NamePrefix,
                            sourceParamName: expandDecl.SourceType.Name,
                            columnTypeTemplate: columnType,
                            visibility: template.Visibility));
                    }
                }
                continue;
            }

            if (member is VariableDeclaration { Type: not null } memberVariable)
            {
                TypeInfo? memberVariableType = ResolveSimpleType(registry: registry,
                    typeExpr: memberVariable.Type,
                    genericParams: genericParams,
                    moduleName: moduleName);
                if (memberVariableType != null)
                {
                    memberVariables.Add(
                        item: new MemberVariableInfo(name: memberVariable.Name,
                            type: memberVariableType)
                        {
                            Visibility = memberVariable.Visibility,
                            HasDefaultValue = memberVariable.Initializer != null,
                            Location = memberVariable.Location
                        });
                }
            }
        }

        return memberVariables;
    }

    /// <summary>
    /// Registers a record type from stdlib.
    /// </summary>
    private static void RegisterRecordType(TypeRegistry registry, RecordDeclaration record,
        string moduleName)
    {
        bool isEntitySpecialization = IsEntityTypeSpecialization(record: record);

        // Skip if already registered (non-entity-specialization types only;
        // entity specializations need separate registration even if the base name exists).
        // Module-QUALIFIED (mirrors RegisterEntityType): a bare-name check depended on the cross-module
        // short-name scan to find the existing registration — with that scan gone a same-module reload
        // would miss and re-register (RF-S "already registered"), and a cross-module same-name type
        // would spuriously skip.
        string qualifiedRecordName = string.IsNullOrEmpty(value: moduleName)
            ? record.Name : $"{moduleName}.{record.Name}";
        if (!isEntitySpecialization
            && registry.LookupType(name: qualifiedRecordName, realm: _registeringRealm ?? "RF") != null)
        {
            return;
        }

        // Build member variables + expand-template list upfront (TypeInfo uses init properties with
        // IReadOnlyList).
        var expandTemplates = new List<MemberExpandTemplateInfo>();
        List<MemberVariableInfo> memberVariables = BuildStdlibMemberVariables(registry: registry,
            members: record.Members, genericParams: record.GenericParameters, moduleName: moduleName,
            expandTemplates: expandTemplates);

        // Resolve implemented protocols (obeys clause)
        var protocols = new List<TypeInfo>();
        foreach (TypeExpression protoExpr in record.Protocols)
        {
            TypeInfo? protoType = ResolveSimpleType(registry: registry,
                typeExpr: protoExpr,
                genericParams: record.GenericParameters,
                moduleName: moduleName);
            if (protoType != null)
            {
                protocols.Add(item: protoType);
            }
        }

        // Inherit CarrierKind from the pre-registered generic definition shell when building
        // entity-type specializations (e.g. Maybe[T] needs T is EntityType).
        CarrierKind inheritedCarrierKind = CarrierKind.None;
        if (isEntitySpecialization &&
            registry.LookupType(name: record.Name) is RecordTypeInfo { CarrierKind: var baseKind })
        {
            inheritedCarrierKind = baseKind;
        }

        var typeInfo = new RecordTypeInfo(name: record.Name)
        {
            Module = moduleName,
            Realm = _registeringRealm ?? "RF",
            Visibility = record.Visibility,
            ImplementedProtocols = protocols,
            ConditionalObeys = BuildConditionalObeys(protoExprs: record.Protocols),
            GenericParameters = record.GenericParameters,
            GenericConstraints = record.GenericConstraints,
            Annotations = record.Annotations,
            BackendType = ExtractLlvmAnnotation(annotations: record.Annotations),
            CarrierKind = inheritedCarrierKind
        };
        if (expandTemplates.Count > 0)
        {
            typeInfo.ExpandTemplates = expandTemplates;
        }

        // Back-fill Owner + Index now that typeInfo exists (Owner is needed for module access checks)
        typeInfo.MemberVariables = memberVariables
                                   .Select(selector: (mv, i) =>
                                        new MemberVariableInfo(name: mv.Name, type: mv.Type)
                                        {
                                            Visibility = mv.Visibility,
                                            Index = i,
                                            HasDefaultValue = mv.HasDefaultValue,
                                            Location = mv.Location,
                                            Owner = typeInfo
                                        })
                                   .ToList();

        RegisterAssociatedTypeBindings(registry: registry,
            declared: record.AssociatedTypes,
            genericParams: record.GenericParameters,
            moduleName: moduleName,
            bindings: typeInfo.AssociatedTypeBindings);

        if (isEntitySpecialization)
        {
            // This is the entity-type specialization of a constrained generic
            // (e.g. Maybe[T] needs T is EntityType -> { Hijacked[T] } layout).
            // Register it so GetOrCreateResolution can select it for entity type arguments.
            registry.RegisterEntitySpecialization(type: typeInfo);
        }
        else
        {
            try
            {
                registry.RegisterType(type: typeInfo);
            }
            catch
            {
                // Ignore duplicate type registration
            }
        }
    }

    /// <summary>
    /// Registers a crashable type from stdlib.
    /// Crashable types are heap-allocated error types that implement the Crashable protocol.
    /// </summary>
    private static void RegisterCrashableType(TypeRegistry registry, CrashableDeclaration crashable,
        string moduleName)
    {
        // Skip if already registered (module-QUALIFIED — see RegisterRecordType).
        string qualifiedCrashableName = string.IsNullOrEmpty(value: moduleName)
            ? crashable.Name : $"{moduleName}.{crashable.Name}";
        if (registry.LookupType(name: qualifiedCrashableName, realm: _registeringRealm ?? "RF") != null)
        {
            return;
        }

        // Resolve member variables (e.g., KeyNotFoundError.key: Text)
        var memberVariables = new List<MemberVariableInfo>();
        foreach (SyntaxTree.Declaration member in crashable.Members)
        {
            if (member is VariableDeclaration { Type: not null } field)
            {
                TypeInfo? memberType = ResolveSimpleType(registry: registry,
                    typeExpr: field.Type,
                    genericParams: null,
                    moduleName: moduleName);
                if (memberType != null)
                {
                    memberVariables.Add(
                        item: new MemberVariableInfo(name: field.Name, type: memberType)
                        {
                            Visibility = field.Visibility,
                            HasDefaultValue = field.Initializer != null,
                            Location = field.Location
                        });
                }
            }
        }

        var typeInfo = new CrashableTypeInfo(name: crashable.Name)
        {
            Module = moduleName,
            Realm = _registeringRealm ?? "RF",
            Visibility = crashable.Visibility,
            Location = crashable.Location
        };

        // Back-fill Owner + Index now that typeInfo exists (Owner is needed for module access checks)
        typeInfo.MemberVariables = memberVariables
                                   .Select(selector: (mv, i) =>
                                        new MemberVariableInfo(name: mv.Name, type: mv.Type)
                                        {
                                            Visibility = mv.Visibility,
                                            Index = i,
                                            HasDefaultValue = mv.HasDefaultValue,
                                            Location = mv.Location,
                                            Owner = typeInfo
                                        })
                                   .ToList();

        try
        {
            registry.RegisterType(type: typeInfo);
        }
        catch
        {
            // Ignore duplicate type registration
        }
    }

    /// <summary>
    /// Registers an entity type from stdlib.
    /// </summary>
    private static void RegisterEntityType(TypeRegistry registry, EntityDeclaration entity,
        string moduleName)
    {
        // Skip if THIS module's type is already registered (idempotency). The check must be
        // module-qualified: a bare-name check would skip a Suflae-realm overlay `entity List` merely
        // because the RazorForge-realm `Core.List` (loaded earlier) shares the bare name, leaving
        // `Suflae.List` unregistered — its constructor/memberRoutines then mis-bind to `Core.List` (spurious
        // RF-S406). Different modules own distinct same-named types.
        string qualifiedName = string.IsNullOrEmpty(value: moduleName)
            ? entity.Name
            : $"{moduleName}.{entity.Name}";
        if (registry.LookupType(name: qualifiedName, realm: _registeringRealm ?? "RF") != null)
        {
            return;
        }

        // Build member variables + expand-template list upfront. Decl-position
        // `expand m in allmemvarof(T)` columns (SoA entity, e.g. a growable SplitList) are resolved
        // here because the stdlib path does NOT run TypeBodyResolver (see RegisterRecordType).
        var expandTemplates = new List<MemberExpandTemplateInfo>();
        List<MemberVariableInfo> memberVariables = BuildStdlibMemberVariables(registry: registry,
            members: entity.Members, genericParams: entity.GenericParameters, moduleName: moduleName,
            expandTemplates: expandTemplates);

        // Resolve implemented protocols (obeys clause)
        var protocols = new List<TypeInfo>();
        foreach (TypeExpression protoExpr in entity.Protocols)
        {
            TypeInfo? protoType = ResolveSimpleType(registry: registry,
                typeExpr: protoExpr,
                genericParams: entity.GenericParameters,
                moduleName: moduleName);
            if (protoType != null)
            {
                protocols.Add(item: protoType);
            }
        }

        var typeInfo = new EntityTypeInfo(name: entity.Name)
        {
            Module = moduleName,
            Realm = _registeringRealm ?? "RF",
            Visibility = entity.Visibility,
            ImplementedProtocols = protocols,
            ConditionalObeys = BuildConditionalObeys(protoExprs: entity.Protocols),
            GenericParameters = entity.GenericParameters,
            GenericConstraints = entity.GenericConstraints
        };
        if (expandTemplates.Count > 0)
        {
            typeInfo.ExpandTemplates = expandTemplates;
        }

        // Back-fill Owner + Index now that typeInfo exists (Owner is needed for module access checks)
        typeInfo.MemberVariables = memberVariables
                                   .Select(selector: (mv, i) =>
                                        new MemberVariableInfo(name: mv.Name, type: mv.Type)
                                        {
                                            Visibility = mv.Visibility,
                                            Index = i,
                                            HasDefaultValue = mv.HasDefaultValue,
                                            Location = mv.Location,
                                            Owner = typeInfo
                                        })
                                   .ToList();

        RegisterAssociatedTypeBindings(registry: registry,
            declared: entity.AssociatedTypes,
            genericParams: entity.GenericParameters,
            moduleName: moduleName,
            bindings: typeInfo.AssociatedTypeBindings);

        registry.RegisterType(type: typeInfo);
    }

    /// <summary>
    /// Extracts the <c>obeys P onlyif (param obeys proto, …)</c> conditional-conformance conditions from a
    /// type header's obeys list into the def-level map the conformance query reads (protocol bare name →
    /// AND-list of <c>(paramName, protocolName)</c>). Returns null when no protocol carries an
    /// <c>onlyif</c> clause, so an unconditional type stores nothing.
    /// </summary>
    internal static Dictionary<string, List<(string ParamName, string ProtocolName)>>? BuildConditionalObeys(
        List<TypeExpression> protoExprs)
    {
        Dictionary<string, List<(string, string)>>? result = null;
        foreach (TypeExpression pe in protoExprs)
        {
            if (pe.ConformanceConditions is not { Count: > 0 } conds)
                continue;
            var list = new List<(string, string)>();
            foreach (GenericConstraintDeclaration c in conds)
                if (c.ConstraintTypes is { Count: > 0 } cts)
                    foreach (TypeExpression proto in cts)
                        list.Add(item: (c.ParameterName, proto.Name));
            (result ??= new Dictionary<string, List<(string, string)>>(comparer: System.StringComparer.Ordinal))
                [key: pe.Name] = list;
        }
        return result;
    }

    /// <summary>
    /// Post-registration pass: (re)resolves associated-type bindings (<c>relates Concrete as Name</c>)
    /// for all entity/record types now that every type — including iterator/emitter types defined
    /// later in their file — is registered. The inline resolution during initial registration can
    /// miss forward references (e.g. <c>List</c> binds <c>ListEmitter[T]</c> but <c>ListEmitter</c>
    /// is declared further down the file), leaving the binding empty; this fills them in.
    /// </summary>
    private static void ResolveAssociatedTypeBindings(TypeRegistry registry, Program program)
    {
        // Find THIS program's types by their module-qualified name. A bare `LookupType(name)` depended on
        // the cross-module short-name scan; with it gone a module type (e.g. `IterTools.WhereIterable`)
        // misses, the `when ... is EntityTypeInfo` guard fails, and the type is SKIPPED — leaving its
        // `relates X as Iter` associated binding empty, so a later `S/Iter` projection never resolves
        // (`WhereIterable[..]/Iter` reaches codegen as an unemittable TypeParameter).
        string? programModule = program.Declarations.OfType<ModuleDeclaration>().FirstOrDefault()?.Path;
        TypeInfo? LookupOwn(string typeName) =>
            (programModule != null ? registry.LookupType(name: $"{programModule}.{typeName}") : null)
            ?? registry.LookupType(name: typeName);

        foreach (ISyntaxTreeNode node in program.Declarations)
        {
            switch (node)
            {
                case EntityDeclaration { AssociatedTypes: { Count: > 0 } at } ed
                    when LookupOwn(ed.Name) is EntityTypeInfo ent:
                    RegisterAssociatedTypeBindings(registry: registry, declared: at,
                        genericParams: ed.GenericParameters, moduleName: ent.Module ?? "",
                        bindings: ent.AssociatedTypeBindings);
                    break;
                case RecordDeclaration { AssociatedTypes: { Count: > 0 } at } rd
                    when LookupOwn(rd.Name) is RecordTypeInfo rec:
                    RegisterAssociatedTypeBindings(registry: registry, declared: at,
                        genericParams: rd.GenericParameters, moduleName: rec.Module ?? "",
                        bindings: rec.AssociatedTypeBindings);
                    break;
            }
        }
    }

    /// <summary>
    /// Resolves <c>relates Concrete as Name</c> bindings from a declaration's AST and populates the
    /// target type's binding map (slot name → concrete <see cref="TypeInfo"/>). Guarded by entity
    /// and record registration.
    /// </summary>
    private static void RegisterAssociatedTypeBindings(TypeRegistry registry,
        List<AssociatedTypeDeclaration>? declared, List<string>? genericParams, string moduleName,
        Dictionary<string, TypeInfo> bindings)
    {
        if (declared is not { Count: > 0 })
        {
            return;
        }

        foreach (AssociatedTypeDeclaration binding in declared)
        {
            if (binding.Binding == null)
            {
                continue;
            }

            TypeInfo? concrete = ResolveSimpleType(registry: registry,
                typeExpr: binding.Binding,
                genericParams: genericParams,
                moduleName: moduleName);
            if (concrete != null)
            {
                bindings[key: binding.Name] = concrete;
            }
        }
    }

    /// <summary>
    /// Registers a choice type from stdlib.
    /// </summary>
    private static void RegisterChoiceType(TypeRegistry registry, ChoiceDeclaration choice,
        string moduleName)
    {
        // Skip if already registered (module-QUALIFIED — see RegisterRecordType).
        string qualifiedChoiceName = string.IsNullOrEmpty(value: moduleName)
            ? choice.Name : $"{moduleName}.{choice.Name}";
        if (registry.LookupType(name: qualifiedChoiceName, realm: _registeringRealm ?? "RF") != null)
        {
            return;
        }

        // Build cases list upfront
        var cases = new List<ChoiceCaseInfo>();
        int autoValue = 0;
        foreach (ChoiceCase caseDecl in choice.Cases)
        {
            int? explicitValue = null;
            if (caseDecl.Value is LiteralExpression { Value: string valStr })
            {
                if (int.TryParse(s: valStr, result: out int v))
                {
                    explicitValue = v;
                }
            }
            else if (caseDecl.Value is UnaryExpression
                     {
                         Operator: UnaryOperator.Minus,
                         Operand: LiteralExpression { Value: string negStr }
                     } &&
                     int.TryParse(s: negStr, result: out int v))
            {
                explicitValue = -v;
            }

            int computedValue;
            if (explicitValue.HasValue)
            {
                computedValue = explicitValue.Value;
                autoValue = computedValue + 1;
            }
            else
            {
                computedValue = autoValue;
                autoValue++;
            }

            cases.Add(item: new ChoiceCaseInfo(name: caseDecl.Name)
            {
                Value = explicitValue, ComputedValue = computedValue
            });
        }

        var typeInfo = new ChoiceTypeInfo(name: choice.Name)
        {
            Module = moduleName, Realm = _registeringRealm ?? "RF", Visibility = choice.Visibility, Cases = cases
        };

        registry.RegisterType(type: typeInfo);
    }

    /// <summary>
    /// Registers a flags type from stdlib.
    /// </summary>
    private static void RegisterFlagsType(TypeRegistry registry, FlagsDeclaration flags,
        string moduleName)
    {
        // Skip if already registered (module-QUALIFIED — see RegisterRecordType).
        string qualifiedFlagsName = string.IsNullOrEmpty(value: moduleName)
            ? flags.Name : $"{moduleName}.{flags.Name}";
        if (registry.LookupType(name: qualifiedFlagsName, realm: _registeringRealm ?? "RF") != null)
        {
            return;
        }

        var members = new List<FlagsMemberInfo>();
        for (int i = 0; i < flags.Members.Count; i++)
        {
            members.Add(item: new FlagsMemberInfo(Name: flags.Members[index: i], BitPosition: i));
        }

        var typeInfo = new FlagsTypeInfo(name: flags.Name)
        {
            Module = moduleName, Realm = _registeringRealm ?? "RF", Visibility = flags.Visibility, Members = members
        };

        registry.RegisterType(type: typeInfo);
    }

    /// <summary>
    /// Registers a variant type (type-based tagged union) from stdlib.
    /// </summary>
    private static void RegisterVariantType(TypeRegistry registry, VariantDeclaration variant,
        string moduleName)
    {
        // Skip if already registered (module-QUALIFIED — see RegisterRecordType).
        string qualifiedVariantName = string.IsNullOrEmpty(value: moduleName)
            ? variant.Name : $"{moduleName}.{variant.Name}";
        if (registry.LookupType(name: qualifiedVariantName, realm: _registeringRealm ?? "RF") != null)
        {
            return;
        }

        List<VariantMemberInfo> members =
            BuildVariantMembers(registry: registry, variant: variant, moduleName: moduleName);

        var typeInfo = new VariantTypeInfo(name: variant.Name)
        {
            Module = moduleName,
            Realm = _registeringRealm ?? "RF",
            Members = members,
            GenericParameters = variant.GenericParameters,
            GenericConstraints = variant.GenericConstraints
        };

        registry.RegisterType(type: typeInfo);
    }

    /// <summary>
    /// Builds a variant's member list: None = tag 0, other arms sequential. An arm whose type does
    /// not resolve yet (a forward or self reference like <c>List[SerialValue]</c> inside SerialValue,
    /// or a not-yet-registered type) is skipped here and picked up when <see
    /// cref="ResolveProgramMemberVariables"/> re-runs after every type shell exists.
    /// </summary>
    private static List<VariantMemberInfo> BuildVariantMembers(TypeRegistry registry,
        VariantDeclaration variant, string? moduleName)
    {
        var members = new List<VariantMemberInfo>();
        int tag = 0;

        if (variant.Members.Any(predicate: m => m.Type.Name == "None"))
        {
            members.Add(item: VariantMemberInfo.CreateNone(ordinal: 0, location: null));
            tag = 1;
        }

        foreach (VariantMember memberDecl in variant.Members.Where(predicate: m => m.Type.Name != "None"))
        {
            TypeInfo? memberType = ResolveSimpleType(registry: registry, typeExpr: memberDecl.Type,
                genericParams: variant.GenericParameters, moduleName: moduleName);
            if (memberType != null)
            {
                members.Add(item: new VariantMemberInfo(type: memberType) { Ordinal = tag++ });
            }
        }

        return members;
    }

    /// <summary>
    /// Registers a protocol type from stdlib (single-pass: registers type and memberRoutines together).
    /// Used by RegisterProgramTypes (pass 1b) for protocols encountered outside the two-pass path.
    /// </summary>
    private static void RegisterProtocolType(TypeRegistry registry, ProtocolDeclaration protocol,
        string moduleName)
    {
        RegisterProtocolTypeShell(registry: registry, protocol: protocol, moduleName: moduleName);
        FillProtocolMemberRoutines(registry: registry, protocol: protocol);
    }

    /// <summary>
    /// Registers a protocol type shell (name, generic params) without memberRoutine signatures.
    /// This is the first pass of protocol registration — ensures all protocol types exist
    /// before memberRoutine signatures are resolved (which may reference other protocols).
    /// </summary>
    private static void RegisterProtocolTypeShell(TypeRegistry registry,
        ProtocolDeclaration protocol, string moduleName)
    {
        // Skip if already registered (module-QUALIFIED — see RegisterRecordType).
        string qualifiedProtocolName = string.IsNullOrEmpty(value: moduleName)
            ? protocol.Name : $"{moduleName}.{protocol.Name}";
        if (registry.LookupType(name: qualifiedProtocolName, realm: _registeringRealm ?? "RF") != null)
        {
            return;
        }

        var typeInfo = new ProtocolTypeInfo(name: protocol.Name)
        {
            Module = moduleName,
            Realm = _registeringRealm ?? "RF",
            Visibility = protocol.Visibility,
            MemberRoutines = [], // Filled in by FillProtocolMemberRoutines
            GenericParameters = protocol.GenericParameters,
            GenericConstraints = protocol.GenericConstraints
        };

        // Associated-type slots declared via `relates Iter obeys Iterator[T]`.
        if (protocol.AssociatedTypes is { Count: > 0 } slots)
        {
            foreach (AssociatedTypeDeclaration slot in slots)
            {
                TypeInfo? constraint = slot.Constraint != null
                    ? ResolveSimpleType(registry: registry,
                        typeExpr: slot.Constraint,
                        genericParams: protocol.GenericParameters,
                        moduleName: moduleName)
                    : null;
                typeInfo.AssociatedTypes.Add(item: new AssociatedTypeSlot(name: slot.Name)
                {
                    Constraint = constraint
                });
            }
        }

        registry.RegisterType(type: typeInfo);
    }

    /// <summary>
    /// Re-resolves protocol memberRoutine return types that failed to resolve during the initial pass
    /// due to forward references (e.g., Crashable.crash_message() -> Text where Text was not
    /// yet registered when protocols were first processed).
    /// Analogous to ResolveProgramMemberVariables for record/entity member variables.
    /// </summary>
    private static void ResolveProtocolMemberRoutineReturnTypes(TypeRegistry registry, Program program)
    {
        foreach (ISyntaxTreeNode node in program.Declarations)
        {
            if (node is not ProtocolDeclaration protocolDecl)
            {
                continue;
            }

            var existing = registry.LookupType(name: protocolDecl.Name) as ProtocolTypeInfo;
            if (existing == null || existing.MemberRoutines.Count == 0)
            {
                continue;
            }

            if (!ProtocolMemberRoutinesNeedRefresh(protocolDecl: protocolDecl, existing: existing))
            {
                continue;
            }

            // Reset and re-fill with all type shells now registered
            existing.MemberRoutines = [];
            FillProtocolMemberRoutines(registry: registry, protocol: protocolDecl);

            // Cached protocol instances (e.g. MutableIndexable[T] created during List's earlier
            // stdlib registration) copied the pre-refill stale memberRoutines. Refresh them in place so
            // user types obeying the protocol see the corrected arity instead of failing S703.
            registry.RefreshProtocolResolutions(genericDef: existing);
        }
    }

    /// <summary>
    /// Whether a registered protocol's member routines must be reset and re-filled: true when any
    /// declared member routine has a param-count mismatch (a param whose type was a forward reference
    /// was silently dropped) or a null return type where the declaration declares one.
    /// </summary>
    private static bool ProtocolMemberRoutinesNeedRefresh(ProtocolDeclaration protocolDecl,
        ProtocolTypeInfo existing)
    {
        foreach (RoutineSignature memberRoutine in protocolDecl.MemberRoutines)
        {
            bool isFailable = memberRoutine.IsFailable;
            string fullName = memberRoutine.Name;
            bool isInstance = fullName.StartsWith(value: "Me.");
            string memberRoutineName = isInstance ? fullName[3..] : fullName;

            ProtocolMemberRoutineInfo? protoMemberRoutine = existing.MemberRoutines.FirstOrDefault(predicate: m =>
                m.Name == memberRoutineName && m.IsFailable == isFailable);

            // A param whose type was a forward reference (e.g. a concrete `index: U64` before
            // U64 was registered) is silently dropped by FillProtocolMemberRoutines, leaving the proto
            // memberRoutine with fewer params than declared. Detect the count mismatch and re-fill now
            // that all type shells exist, so conformance (S703) sees the real arity. This must
            // run for void memberRoutines too (e.g. `setitem!`), so it precedes the return-type check.
            int declParamCount = memberRoutine.Parameters.Count(predicate: p => p.Name != "me");
            if (protoMemberRoutine != null && protoMemberRoutine.ParameterTypes.Count != declParamCount)
            {
                return true;
            }

            if (memberRoutine.ReturnType == null)
            {
                continue; // Intentionally void — nothing more to check for return type
            }

            if (protoMemberRoutine?.ReturnType == null)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Re-resolves routine signatures after all module types are registered.
    /// This repairs stdlib routines that were registered before a referenced return type or
    /// parameter type became available and were later finalized to None/Error.
    /// </summary>
    private static void ResolveRoutineSignatures(TypeRegistry registry, Program program,
        string moduleName)
    {
        foreach (ISyntaxTreeNode node in program.Declarations)
        {
            if (node is not RoutineDeclaration routine)
                continue;
            if (ShouldSkipBuilderQueryRoutineDecl(routine: routine, moduleName: moduleName))
                continue;
            TryUpdateRoutineSignature(registry: registry, routine: routine, moduleName: moduleName);
        }
    }

    /// <summary>
    /// Attempts to re-resolve and update the signature of one routine declaration. Desugar's variadic
    /// params, resolves the owner type (skipping the decl when the owner is not yet registered),
    /// resolves parameters and return type, then updates the existing registry entry when any
    /// parameter has an error type or the return type is missing/None.
    /// </summary>
    private static void TryUpdateRoutineSignature(TypeRegistry registry, RoutineDeclaration routine,
        string moduleName)
    {
        VariadicParamDesugar.Apply(routine: routine);

        string memberRoutineName = routine.MemberRoutineName ?? routine.Name;
        TypeInfo? ownerType = ResolveSignatureOwner(registry: registry, routine: routine,
            moduleName: moduleName);
        if (ownerType == null && routine.RenderedReceiver != null)
            return; // Owner declared but not found — skip this decl.

        List<string>? ctx = BuildSignatureGenericContext(ownerType: ownerType,
            routine: routine);

        List<ParameterInfo> parameters = ResolveRoutineParameters(registry: registry,
            routine: routine, ctx: ctx, moduleName: moduleName);

        TypeInfo? resolvedReturnType = routine.ReturnType != null
            ? ResolveSimpleType(registry: registry, typeExpr: routine.ReturnType,
                genericParams: ctx, moduleName: moduleName)
            : null;

        RoutineInfo? existingRoutine = LookupExistingRoutine(registry: registry,
            ownerType: ownerType, memberRoutineName: memberRoutineName,
            moduleName: moduleName, routine: routine, parameters: parameters);
        if (existingRoutine == null)
            return;

        if (!SignatureNeedsUpdate(existingRoutine: existingRoutine, routine: routine))
            return;

        registry.UpdateRoutine(routine: existingRoutine,
            parameters: parameters,
            returnType: resolvedReturnType,
            genericParameters: existingRoutine.GenericParameters,
            genericConstraints: existingRoutine.GenericConstraints);
    }

    /// <summary>
    /// Resolves the owner type for a routine from its <see cref="RoutineDeclaration.RenderedReceiver"/>.
    /// Returns null both when there is no receiver (free routine) and when the receiver is declared
    /// but not yet registered — callers must distinguish using <see cref="RoutineDeclaration.RenderedReceiver"/>.
    /// </summary>
    private static TypeInfo? ResolveSignatureOwner(TypeRegistry registry, RoutineDeclaration routine,
        string moduleName)
    {
        if (routine.RenderedReceiver is not { } ownerName)
            return null;
        return registry.LookupType(name: ownerName)
            ?? registry.LookupType(name: $"{moduleName}.{ownerName}");
    }

    /// <summary>
    /// Builds a simple (non-receiver-filtered) generic context for signature re-resolution: owner
    /// generic params followed by routine generic params. Receiver-leaf filtering is NOT applied here
    /// because the re-resolution pass already has a concrete owner and does not encounter the
    /// ambiguous-leaf problem that <see cref="BuildRoutineGenericContext"/> solves.
    /// Returns null when there are no generic params in scope.
    /// </summary>
    private static List<string>? BuildSignatureGenericContext(TypeInfo? ownerType,
        RoutineDeclaration routine)
    {
        var genericContext = new List<string>();
        if (ownerType?.GenericParameters != null)
            genericContext.AddRange(collection: ownerType.GenericParameters);
        if (routine.GenericParameters != null)
            genericContext.AddRange(collection: routine.GenericParameters);
        return genericContext.Count > 0 ? genericContext : null;
    }

    /// <summary>
    /// Looks up the existing registered <see cref="RoutineInfo"/> for a routine declaration.
    /// Uses overload resolution when parameters are present, otherwise falls back to name+failable lookup.
    /// </summary>
    private static RoutineInfo? LookupExistingRoutine(TypeRegistry registry, TypeInfo? ownerType,
        string memberRoutineName, string moduleName, RoutineDeclaration routine,
        List<ParameterInfo> parameters)
    {
        string baseName = ownerType != null
            ? $"{ownerType.Name}.{memberRoutineName}"
            : (string.IsNullOrEmpty(value: moduleName)
                ? memberRoutineName
                : $"{moduleName}.{memberRoutineName}");
        return parameters.Count > 0
            ? registry.LookupRoutineOverload(baseName: baseName,
                argTypes: parameters.Select(selector: p => p.Type).ToList())
            : registry.LookupRoutine(fullName: baseName, isFailable: routine.IsFailable);
    }

    /// <summary>
    /// Returns true when the existing routine's signature has a parameter typed as
    /// <see cref="ErrorTypeInfo"/> or is missing a declared return type (null/Error/None),
    /// indicating a re-resolution update is warranted.
    /// </summary>
    private static bool SignatureNeedsUpdate(RoutineInfo existingRoutine, RoutineDeclaration routine)
    {
        bool hasErrorParams = existingRoutine.Parameters.Any(predicate: p => p.Type is ErrorTypeInfo);
        bool missingReturn = routine.ReturnType != null
            && (existingRoutine.ReturnType == null
                || existingRoutine.ReturnType is ErrorTypeInfo
                || existingRoutine.ReturnType.Name == "None");
        return hasErrorParams || missingReturn;
    }

    /// <summary>
    /// Fills in memberRoutine signatures for a previously registered protocol type.
    /// This is the second pass — all protocols are registered, so cross-references resolve.
    /// </summary>
    private static void FillProtocolMemberRoutines(TypeRegistry registry, ProtocolDeclaration protocol)
    {
        var existing = registry.LookupType(name: protocol.Name) as ProtocolTypeInfo;
        if (existing == null || existing.MemberRoutines.Count > 0)
        {
            return; // Already has memberRoutines or not found
        }

        var memberRoutines = new List<ProtocolMemberRoutineInfo>();
        foreach (RoutineSignature memberRoutine in protocol.MemberRoutines)
        {
            AppendProtocolMemberRoutine(registry: registry, protocol: protocol,
                memberRoutine: memberRoutine, memberRoutines: memberRoutines);
        }

        existing.MemberRoutines = memberRoutines;
    }

    /// <summary>
    /// Resolves one protocol memberRoutine signature into a <see cref="ProtocolMemberRoutineInfo"/>
    /// (appended to <paramref name="memberRoutines"/>). For a failable memberRoutine also appends the
    /// auto-derived <c>try_X</c> non-failable variant returning Maybe[T] (or Bool when T is None).
    /// </summary>
    private static void AppendProtocolMemberRoutine(TypeRegistry registry, ProtocolDeclaration protocol,
        RoutineSignature memberRoutine, List<ProtocolMemberRoutineInfo> memberRoutines)
    {
        bool isFailable = memberRoutine.IsFailable;
        string fullName = memberRoutine.Name;
        bool isInstance = fullName.StartsWith(value: "Me.");
        string memberRoutineName = isInstance ? fullName[3..] : fullName;

        TypeInfo? rawReturnType = memberRoutine.ReturnType != null
            ? ResolveSimpleType(registry: registry,
                typeExpr: memberRoutine.ReturnType,
                genericParams: protocol.GenericParameters)
            : null;
        TypeInfo? resolvedReturnType = memberRoutine.ReturnType?.Name == "Me"
            ? ProtocolSelfTypeInfo.Instance
            : rawReturnType;

        ResolveProtocolParamTypes(registry: registry, protocol: protocol,
            memberRoutine: memberRoutine,
            parameterTypes: out List<TypeInfo> parameterTypes,
            parameterNames: out List<string> parameterNames);

        memberRoutines.Add(item: new ProtocolMemberRoutineInfo(name: memberRoutineName)
        {
            IsInstanceMemberRoutine = isInstance,
            ParameterTypes = parameterTypes,
            ParameterNames = parameterNames,
            ReturnType = resolvedReturnType,
            IsFailable = isFailable
        });

        if (isFailable)
        {
            AppendTryVariant(registry: registry, memberRoutineName: memberRoutineName,
                isInstance: isInstance, parameterTypes: parameterTypes,
                parameterNames: parameterNames, resolvedReturnType: resolvedReturnType,
                memberRoutines: memberRoutines);
        }
    }

    /// <summary>
    /// Resolves the non-me parameters of a protocol routine signature into parallel
    /// <paramref name="parameterTypes"/> and <paramref name="parameterNames"/> lists.
    /// Parameters named "me" are skipped; "Me"-typed parameters resolve to
    /// <see cref="ProtocolSelfTypeInfo.Instance"/>.
    /// </summary>
    private static void ResolveProtocolParamTypes(TypeRegistry registry, ProtocolDeclaration protocol,
        RoutineSignature memberRoutine,
        out List<TypeInfo> parameterTypes, out List<string> parameterNames)
    {
        parameterTypes = [];
        parameterNames = [];
        foreach (Parameter param in memberRoutine.Parameters)
        {
            if (param.Name == "me")
                continue;
            TypeInfo? paramType = param.Type?.Name == "Me"
                ? ProtocolSelfTypeInfo.Instance
                : ResolveSimpleType(registry: registry,
                    typeExpr: param.Type,
                    genericParams: protocol.GenericParameters);
            if (paramType != null)
            {
                parameterTypes.Add(item: paramType);
                parameterNames.Add(item: param.Name);
            }
        }
    }

    /// <summary>
    /// Appends the auto-derived <c>try_X</c> non-failable variant for a failable protocol routine.
    /// Returns Maybe[T] (or Bool when T is None), mirroring ErrorHandlingGenerator.GenerateTryVariant.
    /// Exposes the variant so call sites typed against the bare protocol (e.g. for-loop desugaring's
    /// <c>iter.try_emit()</c> where <c>iter: Iterator[T]</c>) can resolve.
    /// </summary>
    private static void AppendTryVariant(TypeRegistry registry, string memberRoutineName,
        bool isInstance, List<TypeInfo> parameterTypes, List<string> parameterNames,
        TypeInfo? resolvedReturnType, List<ProtocolMemberRoutineInfo> memberRoutines)
    {
        string tryName = "try_" + memberRoutineName;
        TypeInfo? tryReturnType;
        if (resolvedReturnType == null || resolvedReturnType.Name == "None")
        {
            tryReturnType = registry.LookupType(name: "Bool");
        }
        else
        {
            TypeInfo? maybeDef = registry.LookupType(name: "Maybe");
            tryReturnType = maybeDef != null
                ? registry.GetOrCreateResolution(genericDef: maybeDef,
                    typeArguments: [resolvedReturnType])
                : null;
        }
        memberRoutines.Add(item: new ProtocolMemberRoutineInfo(name: tryName)
        {
            IsInstanceMemberRoutine = isInstance,
            ParameterTypes = parameterTypes,
            ParameterNames = parameterNames,
            ReturnType = tryReturnType,
            IsFailable = false,
            IsAutoDerivedVariant = true
        });
    }
}
