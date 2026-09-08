using Compiler.Declaration;
using TypeModel.Enums;
using TypeModel.Symbols;

namespace TypeModel.Types;

/// <summary>
/// Type information for entities (reference types, heap-allocated).
/// Entity variables cannot be reassigned - they have stable identity.
/// </summary>
public class EntityTypeInfo : TypeInfo
{
    /// <inheritdoc/>
    public override TypeCategory Category => TypeCategory.Entity;

    /// <inheritdoc/>
    public override bool ImplicitConstructorReturnsInFlight => true;

    /// <summary>MemberVariables declared in this entity.</summary>
    public List<MemberVariableInfo> MemberVariables { get; set; } = [];

    /// <summary>Decl-position <c>expand</c> column templates (SoA layout). Populated only on a generic
    /// definition; the registry materializes one member per source-type field at instantiation.</summary>
    public List<MemberExpandTemplateInfo> ExpandTemplates { get; set; } = [];

    /// <summary>Protocols this entity implements (obeys).</summary>
    public List<TypeInfo> ImplementedProtocols { get; set; } = [];

    /// <summary>Conditional-conformance conditions from an <c>obeys P onlyif (param obeys proto, …)</c>
    /// clause, stored on the generic DEFINITION and keyed by the conditionally-obeyed protocol's bare name.
    /// Each entry is the AND-list of <c>(paramName, protocolName)</c> conditions. A concrete instance obeys
    /// that protocol only when every condition holds for its bound type args. Null/absent = unconditional.</summary>
    public Dictionary<string, List<(string ParamName, string ProtocolName)>>? ConditionalObeys { get; set; }

    /// <summary>
    /// Associated-type bindings declared via <c>relates Concrete as Name</c> — maps a protocol
    /// slot name (e.g. <c>Iter</c>) to the concrete type that fills it (e.g. <c>ListEmitter[T]</c>).
    /// Resolved during monomorphization when projecting <c>S/Iter</c>.
    /// </summary>
    public Dictionary<string, TypeInfo> AssociatedTypeBindings { get; set; } = new();

    /// <summary>
    /// Size of the underlying heap-allocated struct (sum of member sizes with alignment).
    /// Distinct from <c>SizeBytes</c>, which returns the pointer-sized value used
    /// to pass entities around at the SSA level.
    /// </summary>
    public int HeapBlockSize(int pointerSize)
    {
        int size = 0;
        int maxAlignment = 1;
        foreach (MemberVariableInfo mv in MemberVariables)
        {
            int memberSize = mv.Type.SizeBytes(pointerSize: pointerSize);
            int alignment = Math.Max(val1: Math.Min(val1: memberSize, val2: 16), val2: 1);
            maxAlignment = Math.Max(val1: maxAlignment, val2: alignment);
            size = AlignTo(size: size, alignment: alignment);
            size += memberSize;
        }
        return AlignTo(size: size, alignment: maxAlignment);
    }

    /// <summary>
    /// For generic definitions, the original generic type this was resolved from.
    /// </summary>
    public EntityTypeInfo? GenericDefinition { get; init; }

    /// <summary>
    /// Looks up a member variable by name in this entity.
    /// </summary>
    /// <param name="memberVariableName">The name of the member variable to look up.</param>
    /// <returns>The member variable info if found, null otherwise.</returns>
    public MemberVariableInfo? LookupMemberVariable(string memberVariableName)
    {
        return MemberVariables.FirstOrDefault(predicate: f => f.Name == memberVariableName);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="EntityTypeInfo"/> class.
    /// </summary>
    /// <param name="name">The name of the entity type.</param>
    public EntityTypeInfo(string name) : base(name: name)
    {
    }

    /// Tracks in-progress CreateInstance calls to break cycles from self-referential types
    /// (e.g., BTreeListNode[T] containing List[BTreeListNode[T]]).
    [ThreadStatic]
    private static HashSet<string>? _creatingInstances;

    /// Maps resolvedName -> the partially-built EntityTypeInfo for that name, so recursive
    /// self-references in member types return the same object rather than an empty shell.
    /// This ensures BTreeListNode[T].children has a List whose element type IS the outer
    /// BTreeListNode[T] instance, not a zero-member placeholder.
    [ThreadStatic]
    private static Dictionary<string, EntityTypeInfo>? _inProgressEntities;

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">Thrown if this is not a generic definition.</exception>
    /// <exception cref="ArgumentException">Thrown if the number of type arguments doesn't match.</exception>
    public override TypeInfo CreateInstance(List<TypeInfo> typeArguments)
    {
        if (!IsGenericDefinition)
        {
            throw new InvalidOperationException(
                message: $"Entity '{Name}' is not a generic definition.");
        }

        if (typeArguments.Count != GenericParameters!.Count)
        {
            throw new ArgumentException(
                message:
                $"Expected {GenericParameters.Count} type arguments, got {typeArguments.Count}.");
        }

        // Build resolved type name using FullName for each type argument so the resolved
        // type carries fully-qualified inner names (e.g., "List[Core.S64]").
        string resolvedName = $"{Name}[{string.Join(separator: ", ",
            values: typeArguments.Select(selector: t => t.FullName))}]";

        string cycleKey = BuildCycleKey(resolvedName: resolvedName);

        // Build the substitution map up front so it can be applied to ImplementedProtocols
        // as well as member-variable types. Without substituting protocols, an obeys-clause
        // referencing a concrete type that does not appear in the entity's own generic
        // parameter list (e.g. `entity BitArrayIterator[N] obeys Iterator[Bool]`) leaves the
        // protocol's own type arguments un-resolved on the concrete instance, and downstream
        // positional matching collapses the protocol arg into the entity's first slot.
        var substitution = new Dictionary<string, TypeInfo>();
        for (int i = 0; i < GenericParameters.Count; i++)
        {
            substitution[key: GenericParameters[index: i]] = typeArguments[index: i];
        }

        var substitutedProtocols = ImplementedProtocols
            .Select(selector: p => (TypeInfo)(ProtocolTypeInfo)RecordTypeInfo.SubstituteType(type: p, substitution: substitution))
            .ToList();

        // Substitute the entity's generic params into each associated-type binding
        // (e.g. `relates ListEmitter[T] as Iter` becomes `Iter -> ListEmitter[S64]`).
        var substitutedBindings = AssociatedTypeBindings.ToDictionary(
            keySelector: kv => kv.Key,
            elementSelector: kv =>
                RecordTypeInfo.SubstituteType(type: kv.Value, substitution: substitution));

        // Detect cycles from self-referential member types (e.g., BTreeListNode[T].children:
        // List[BTreeListNode[T]]). Return the in-progress entity so the recursive reference
        // points to the same object that will have its members filled in below.
        _creatingInstances ??= [];
        _inProgressEntities ??= new Dictionary<string, EntityTypeInfo>();
        if (!_creatingInstances.Add(item: cycleKey))
        {
            // Return the partially-built entity if available; a fresh empty shell otherwise
            // (the shell case should not normally occur since we always register below first).
            return _inProgressEntities.TryGetValue(key: cycleKey, value: out EntityTypeInfo? inProgress)
                ? inProgress
                : BuildEntityShell(resolvedName: resolvedName,
                    substitutedProtocols: substitutedProtocols,
                    substitutedBindings: substitutedBindings,
                    typeArguments: typeArguments);
        }

        // Create the entity shell BEFORE substituting member types so that any recursive
        // reference encountered during substitution (cycle detected above) returns this
        // same object — which will have its members populated by the time callers use it.
        var entity = BuildEntityShell(resolvedName: resolvedName,
            substitutedProtocols: substitutedProtocols,
            substitutedBindings: substitutedBindings,
            typeArguments: typeArguments);
        _inProgressEntities[key: cycleKey] = entity;

        try
        {
            // Substitute types in member variables; self-referential inner types resolve to
            // `entity` via the cycle detection path above rather than an empty shell.
            entity.MemberVariables = MemberVariables
                                     .Select(selector: f =>
                                          SubstituteMemberVariableType(memberVariable: f,
                                              substitution: substitution))
                                     .ToList();

            return entity;
        }
        finally
        {
            _creatingInstances.Remove(item: cycleKey);
            _inProgressEntities.Remove(key: cycleKey);
        }
    }

    // The cycle-detection maps are [ThreadStatic] and SHARED across every entity definition,
    // so they must be keyed by the MODULE-QUALIFIED resolved name. Two same-Named defs from
    // different modules/realms — RazorForge `Core.List` and the Suflae-realm overlay `Suflae.List`
    // — both produce the bare `List[Core.S32]`; without the qualifier, a re-entrant instantiation
    // of one (triggered while substituting a field of the other) hits the "already in progress"
    // branch and returns the WRONG module's in-progress entity. The entity's own Name stays bare
    // (it drives LLVM mangling and the resolution-cache short alias).
    // …and REALM-qualified too: `Core.List` exists in BOTH the RazorForge realm (real list) and the
    // Suflae realm (the `{ inner }` wrapper). They share module AND bare resolved name, so a realm-free
    // cycleKey lets an in-progress SF `Core.List[S32]` be handed back for an RF `Core.List[S32]`
    // CreateInstance (and vice versa) — the wrapper's `RF::Core.List[T]()` inner would then resolve to
    // the SF entity and self-recurse. Prefix the non-ambient realm to keep the two world-lines distinct.
    private string BuildCycleKey(string resolvedName)
    {
        string moduleQualified = string.IsNullOrEmpty(value: Module) ? resolvedName : $"{Module}.{resolvedName}";
        return Realm == "RF" ? moduleQualified : $"{Realm}::{moduleQualified}";
    }

    // Builds an empty-membered entity shell carrying this definition's provenance. Used both for the
    // cycle-detected fallback and for the pre-substitution shell (whose members are populated later).
    private EntityTypeInfo BuildEntityShell(string resolvedName,
        List<TypeInfo> substitutedProtocols,
        Dictionary<string, TypeInfo> substitutedBindings,
        List<TypeInfo> typeArguments)
    {
        return new EntityTypeInfo(name: resolvedName)
        {
            MemberVariables = [],
            ImplementedProtocols = substitutedProtocols,
            AssociatedTypeBindings = substitutedBindings,
            TypeArguments = typeArguments,
            GenericDefinition = this,
            Visibility = Visibility,
            Location = Location,
            Module = Module,
            Realm = Realm
        };
    }

    /// <summary>
    /// Substitutes the type in a member variable for generic resolution.
    /// </summary>
    /// <param name="memberVariable">The member variable to substitute.</param>
    /// <param name="substitution">The type parameter substitution map.</param>
    /// <returns>A new <see cref="MemberVariableInfo"/> with the substituted type.</returns>
    private static MemberVariableInfo SubstituteMemberVariableType(
        MemberVariableInfo memberVariable, Dictionary<string, TypeInfo> substitution)
    {
        TypeInfo substitutedType =
            SubstituteType(type: memberVariable.Type, substitution: substitution);
        return memberVariable.WithSubstitutedType(newType: substitutedType);
    }

    /// <summary>
    /// Recursively substitutes type parameters in a type.
    /// </summary>
    /// <param name="type">The type to substitute.</param>
    /// <param name="substitution">The type parameter substitution map.</param>
    /// <returns>The substituted type, or the original if no substitution applies.</returns>
    private static TypeInfo SubstituteType(TypeInfo type,
        Dictionary<string, TypeInfo> substitution)
    {
        if (substitution.TryGetValue(key: type.Name, value: out TypeInfo? substituted))
        {
            return substituted;
        }

        if (!type.IsGenericResolution || type.TypeArguments == null)
        {
            return type;
        }

        return SubstituteGenericResolution(type: type, substitution: substitution);
    }

    // Substitute a generic resolution's args and re-resolve through the ambient registry per kind.
    private static TypeInfo SubstituteGenericResolution(TypeInfo type,
        Dictionary<string, TypeInfo> substitution)
    {
        var newArgs = type.TypeArguments!
                          .Select(selector: arg =>
                               SubstituteType(type: arg, substitution: substitution))
                          .ToList();

        // Route through the ambient TypeRegistry so nested generic resolutions
        // (e.g. Owned[BTreeDictNode[S64, S64]] inside SortedDict[S64, S64]'s root field)
        // get registered and picked up by the monomorphization planner.
        TypeRegistry? registry = TypeRegistry.Ambient;

        if (type is EntityTypeInfo { GenericDefinition: not null } entityType)
        {
            return registry != null
                ? registry.GetOrCreateResolution(genericDef: entityType.GenericDefinition, typeArguments: newArgs)
                : entityType.GenericDefinition.CreateInstance(typeArguments: newArgs);
        }

        if (type is RecordTypeInfo { GenericDefinition: not null } recordType)
        {
            return registry != null
                ? registry.GetOrCreateResolution(genericDef: recordType.GenericDefinition, typeArguments: newArgs)
                : recordType.GenericDefinition.CreateInstance(typeArguments: newArgs);
        }

        if (type is ProtocolTypeInfo { GenericDefinition: not null } protocolType)
        {
            return registry != null
                ? registry.GetOrCreateResolution(genericDef: protocolType.GenericDefinition, typeArguments: newArgs)
                : protocolType.GenericDefinition.CreateInstance(typeArguments: newArgs);
        }

        if (type is WrapperTypeInfo wrapperType)
        {
            return wrapperType.CreateInstance(typeArguments: newArgs);
        }

        return type;
    }
}
