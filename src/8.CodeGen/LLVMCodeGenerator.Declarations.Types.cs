using System.Text;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Compiler.CodeGen;

/// <summary>
/// Declaration code generation for LLVM types and routine signatures.
/// </summary>
public partial class LlvmCodeGenerator
{
    private void GenerateEntityType(EntityTypeInfo entity)
    {
        string typeName = RawEntityTypeName(entity: entity);
        entity = RefreshEntityMembers(entity: entity);

        // Skip if already generated
        if (!_generatedTypes.Add(item: typeName))
        {
            return;
        }

        // Recursively ensure struct types for member variable types are defined
        EnsureMemberVariableTypesGenerated(memberVariables: entity.MemberVariables);

        // Empty struct needs at least a dummy byte for addressability.
        string decl = BuildStructTypeDeclaration(typeName: typeName,
            memberVariables: entity.MemberVariables,
            fieldTypeSelector: mv => GetLlvmType(type: mv.Type),
            emptyBody: "{ i8 }");
        _typeDeclarationsEntity[key: typeName] = decl;
    }

    /// <summary>
    /// Refreshes an entity's member variables when a generic resolution was created before its
    /// definition's members were populated — re-creating from the definition, rebuilding from the
    /// AST, or re-looking-up from the registry.
    /// </summary>
    private EntityTypeInfo RefreshEntityMembers(EntityTypeInfo entity)
    {
        if (entity is
            {
                IsGenericResolution: true, MemberVariables.Count: 0,
                GenericDefinition: { MemberVariables.Count: > 0 } genDef,
                TypeArguments: not null
            }
            && genDef.CreateInstance(typeArguments: entity.TypeArguments)
                is EntityTypeInfo { MemberVariables.Count: > 0 } refreshed)
        {
            entity = refreshed;
        }

        // Structural re-lookup ONLY (a registered entity carries its members). No AST rebuild / no
        // name-based type re-resolution: codegen consumes resolved TypeInfo, it does not reconstruct it.
        if (entity.MemberVariables.Count == 0 &&
            (_registry.LookupType(name: entity.FullName) ?? _registry.LookupType(name: entity.Name))
                is EntityTypeInfo { MemberVariables.Count: > 0 } resolvedEntity)
        {
            entity = resolvedEntity;
        }
        return entity;
    }

    /// <summary>
    /// Builds an LLVM struct type declaration line plus its documentation comment mapping field
    /// index to member-variable name. <paramref name="emptyBody"/> is the struct body used when the
    /// type has no members (e.g. <c>{ i8 }</c> for entities, <c>{ }</c> for records).
    /// </summary>
    private static string BuildStructTypeDeclaration(string typeName,
        List<MemberVariableInfo> memberVariables,
        Func<MemberVariableInfo, string> fieldTypeSelector, string emptyBody, bool packed = false)
    {
        var decl = new StringBuilder();
        if (memberVariables.Count == 0)
        {
            decl.AppendLine(value: $"{typeName} = type {emptyBody}");
            return decl.ToString();
        }

        string memberVars = string.Join(separator: ", ",
            values: memberVariables.Select(selector: fieldTypeSelector));
        // @layout("packed") → LLVM native packed struct `<{ ... }>`: no inter-field padding, so field
        // offsets/GEP match the packed C layout without any explicit padding members.
        string body = packed ? $"<{{ {memberVars} }}>" : $"{{ {memberVars} }}";
        decl.AppendLine(value: $"{typeName} = type {body}");

        decl.Append(handler: $"; {typeName} member variables: ");
        for (int i = 0; i < memberVariables.Count; i++)
        {
            if (i > 0)
            {
                decl.Append(value: ", ");
            }
            decl.Append(handler: $"{i}={memberVariables[index: i].Name}");
        }
        decl.AppendLine();
        return decl.ToString();
    }

    /// <summary>
    /// Generates the LLVM struct type for a crashable type.
    /// Crashable types have entity semantics (heap-allocated, pointer at usage sites).
    /// </summary>
    private void GenerateCrashableType(CrashableTypeInfo crashable)
    {
        string typeName = RawCrashableTypeName(crashable: crashable);

        if (_generatedTypes.Contains(item: typeName))
            return;

        _generatedTypes.Add(item: typeName);

        EnsureMemberVariableTypesGenerated(memberVariables: crashable.MemberVariables);

        var memberVariableTypes = new List<string>();
        foreach (MemberVariableInfo memberVariable in crashable.MemberVariables)
        {
            memberVariableTypes.Add(item: GetLlvmType(type: memberVariable.Type));
        }

        var decl = new StringBuilder();
        if (memberVariableTypes.Count == 0)
        {
            decl.AppendLine(value: $"{typeName} = type {{ i8 }}");
        }
        else
        {
            string memberVars = string.Join(separator: ", ", values: memberVariableTypes);
            decl.AppendLine(value: $"{typeName} = type {{ {memberVars} }}");
        }

        _typeDeclarationsCrashable[key: typeName] = decl.ToString();
    }

    /// <summary>
    /// Generates the LLVM struct type for a record.
    /// Record = value type, stack-allocated, copy semantics.
    /// Single-member-variable wrappers are unwrapped to their underlying intrinsic.
    /// </summary>
    /// <param name="record">The record type info.</param>
    private void GenerateRecordType(RecordTypeInfo record)
    {
        // Backend-annotated records don't need struct types.
        // Skip generic definitions and any type whose type arguments still contain unresolved
        // generic parameters (they produce invalid IR). Defence-in-depth: this runs both from
        // GenerateTypeDeclarations and as a body-emission side-effect.
        if (ShouldSkipRecordTypeGeneration(record: record))
        {
            return;
        }

        string typeName = GetRecordTypeName(record: record);

        // Skip if already generated
        if (!_generatedTypes.Add(item: typeName))
        {
            return;
        }

        // Result[T] / Lookup[T] now declare their inline payload buffer as a real RF field
        // (`payload: Array[U8, ${max(T.data_size().byte_size(), 8)}]`), so the ordinary record layout
        // below produces the same `{ i64 type_id, [N x i8] payload }` the codegen-owned synthesis used
        // to build. No carrier special-case here anymore.
        record = RefreshRecordMembers(record: record);

        // Recursively ensure struct types for member variable types are defined
        EnsureMemberVariableTypesGenerated(memberVariables: record.MemberVariables);

        // Build the struct type. Bool fields use their i8 STORAGE type (not i1) — see
        // GetFieldStorageLlvmType.
        _typeDeclarationsRecord[key: typeName] = BuildStructTypeDeclaration(typeName: typeName,
            memberVariables: record.MemberVariables,
            fieldTypeSelector: mv => GetFieldStorageLlvmType(type: mv.Type),
            emptyBody: "{ }", packed: record.IsPacked);
    }

    /// <summary>
    /// Whether a record needs no struct type generated: backend-annotated types and generic
    /// definitions / partially-concrete resolutions (whose layout would be invalid IR).
    /// </summary>
    private static bool ShouldSkipRecordTypeGeneration(RecordTypeInfo record)
    {
        return record.BackendType != null ||
            record.IsGenericDefinition ||
            record.TypeArguments?.Any(predicate: t =>
                ContainsGenericParameter(t) || t is ErrorTypeInfo ||
                ContainsAbstractProjection(t)) == true;
    }


    /// <summary>
    /// Refreshes a record's member variables when a generic resolution was created before its
    /// definition's members were populated — re-creating from the definition.
    /// </summary>
    private static RecordTypeInfo RefreshRecordMembers(RecordTypeInfo record)
    {
        if (record is
            {
                IsGenericResolution: true, MemberVariables.Count: 0,
                GenericDefinition: { MemberVariables.Count: > 0 } genDef,
                TypeArguments: not null
            }
            && genDef.CreateInstance(typeArguments: record.TypeArguments)
                is RecordTypeInfo { MemberVariables.Count: > 0 } refreshed)
        {
            return refreshed;
        }
        return record;
    }

    /// <summary>
    /// Recursively ensures struct type definitions exist for member variable types.
    /// Handles nested generic resolutions that may not be in the registry (e.g.,
    /// Maybe[BTreeSetNode[S64]] created during member variable substitution).
    /// </summary>
    private void EnsureMemberVariableTypesGenerated(
        List<MemberVariableInfo> memberVariables)
    {
        var visited = new HashSet<string>();
        foreach (MemberVariableInfo mv in memberVariables)
        {
            EnsureTypeGenerated(type: mv.Type, visited: visited);
        }
    }

    // Recursively descends into a type's TypeArguments and wrapper inner types so that
    // concrete nested generics (e.g. Owned[BTreeDictNode[S64, S64]] inside a
    // Maybe[Owned[...]] field of SortedDict[S64, S64]) get their struct types emitted.
    private void EnsureTypeGenerated(TypeInfo? type, HashSet<string> visited)
    {
        if (type == null) return;
        if (!visited.Add(item: type.FullName)) return;

        // Skip not-yet-concrete generic resolutions. A `ListNode[T]` resolution (where T is
        // still `GenericParameterTypeInfo`) has IsGenericDefinition=false but its type
        // arguments include an unbound parameter; emitting its layout would force `T`
        // through GetLlvmType and crash. The same filter applies in GenerateTypeDeclarations
        // for top-level emission — replicate it here so it also gates recursive descent
        // into nested field types of monomorphized parents.
        bool hasUnboundTypeArg =
            type.TypeArguments is { Count: > 0 } args
            && args.Any(predicate: ContainsGenericParameter);

        switch (type)
        {
            // NOTE: no Entity/Crashable case. Those are reference types — they appear as `ptr` in a
            // containing struct's layout, so an enclosing record/entity never needs their struct
            // emitted. Their structs are emitted on-demand at the actual use sites (alloc /
            // field-access / size GEP) via GetEntityTypeName / GetCrashableTypeName. Recursing into
            // them here would drag the whole reference-type graph (BTreeNode, SortedList, ...) into
            // every build.
            case RecordTypeInfo
            {
                IsGenericDefinition: false, BackendType: null
            } nestedRecord when !hasUnboundTypeArg:
                GenerateRecordType(record: nestedRecord);
                break;
        }

        if (type is WrapperTypeInfo wrapper)
        {
            EnsureTypeGenerated(type: wrapper.InnerType, visited: visited);
        }

        if (type.TypeArguments is { Count: > 0 } typeArgs)
        {
            foreach (TypeInfo ta in typeArgs)
            {
                EnsureTypeGenerated(type: ta, visited: visited);
            }
        }
    }

    /// <summary>
    /// Generates the LLVM type for a variant (type-based tagged union).
    /// Variant = { i64 tag, [N x i8] payload } where N = max member size.
    /// </summary>
    /// <param name="variant">The variant type info.</param>
    private void GenerateVariantType(VariantTypeInfo variant)
    {
        string typeName = RawVariantTypeName(variant: variant);

        // Skip if already generated
        if (!_generatedTypes.Add(item: typeName))
        {
            return;
        }

        // Calculate max payload size
        int maxPayloadSize = 0;
        foreach (VariantMemberInfo member in variant.Members)
        {
            if (member is not { IsNone: false, Type: not null })
            {
                continue;
            }

            int payloadSize = GetTypeSize(type: member.Type);
            maxPayloadSize = Math.Max(val1: maxPayloadSize, val2: payloadSize);
        }

        var decl = new StringBuilder();
        // Variant is { i64 tag, [N x i8] payload }
        if (maxPayloadSize > 0)
        {
            decl.AppendLine(value: $"{typeName} = type {{ i64, [{maxPayloadSize} x i8] }}");
        }
        else
        {
            decl.AppendLine(value: $"{typeName} = type {{ i64 }}");
        }

        // Add member info as comment
        decl.Append(handler: $"; {typeName} members: ");
        for (int i = 0; i < variant.Members.Count; i++)
        {
            if (i > 0) decl.Append(value: ", ");
            VariantMemberInfo m = variant.Members[index: i];
            decl.Append(handler: $"{m.Name}={m.Ordinal}");
        }

        decl.AppendLine();

        _typeDeclarationsVariant[key: typeName] = decl.ToString();
    }
}
