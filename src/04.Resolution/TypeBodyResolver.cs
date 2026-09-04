using System.Collections.Generic;
using System.Linq;
using Compiler.Diagnostics;
using Verification;
using Verification.Enums;
using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Compiler.Resolution;

using TypeSymbol = TypeInfo;

/// <summary>
/// Handles resolution of type bodies (member variables, protocols, variants, etc.)
/// for the semantic analyzer.
/// </summary>
internal sealed class TypeBodyResolver
{
    private readonly SemanticVerifier _sa;
    private readonly TypeResolver _typeResolver;

    internal TypeBodyResolver(SemanticVerifier sa, TypeResolver typeResolver)
    {
        _sa = sa;
        _typeResolver = typeResolver;
    }

    #region Phase 4: Type Body Resolution

    /// <summary>
    /// Looks up a type by name, trying the current module-qualified name first,
    /// then falling back to the bare name. This is needed because Phase 3 registers
    /// user types with their full name (e.g., "Module.Point"), but Phase 4 resolvers
    /// only have the bare name from the AST node.
    /// </summary>
    private TypeInfo? LookupTypeInCurrentModule(string name)
    {
        string? moduleName = _sa.GetCurrentModuleName();
        if (moduleName != null)
        {
            TypeInfo? qualified = _sa._registry.LookupType(name: $"{moduleName}.{name}");
            if (qualified != null)
            {
                return qualified;
            }
        }

        return _sa._registry.LookupType(name: name);
    }

    /// <summary>
    /// Resolves type bodies including member variables and memberRoutine signatures.
    /// </summary>
    /// <param name="program">The program to resolve.</param>
    internal void ResolveTypeBodies(Program program)
    {
        foreach (ISyntaxTreeNode declaration in program.Declarations)
        {
            ResolveTypeBody(node: declaration);
        }
    }

    private void ResolveTypeBody(ISyntaxTreeNode node)
    {
        switch (node)
        {
            case RecordDeclaration record:
                ResolveRecordBody(record: record);
                break;

            case EntityDeclaration entity:
                ResolveEntityBody(entity: entity);
                break;

            case ProtocolDeclaration protocol:
                ResolveProtocolBody(protocol: protocol);
                break;

            case VariantDeclaration variant:
                ResolveVariantBody(variant: variant);
                break;

            case ChoiceDeclaration choice:
                ResolveChoiceBody(choice: choice);
                break;

            case FlagsDeclaration flags:
                ResolveFlagsBody(flags: flags);
                break;

            case CrashableDeclaration crashable:
                ResolveCrashableBody(crashable: crashable);
                break;
        }
    }

    private void ResolveRecordBody(RecordDeclaration record)
    {
        if (record.Members.Count == 0 && !record.HasPassBody)
        {
            _sa.ReportError(code: SemanticDiagnosticCode.EmptyBlockWithoutPass,
                message: "Empty record body requires 'pass' keyword.",
                location: record.Location);
        }

        TypeSymbol? previousType = _sa._currentType;
        HashSet<string>? previousMemberVariableNames = _sa._currentTypeMemberVariableNames;

        _sa._currentType = LookupTypeInCurrentModule(name: record.Name);
        _sa._currentTypeMemberVariableNames = [];

        // Resolve implemented protocols
        if (_sa._currentType is RecordTypeInfo && record.Protocols.Count > 0)
        {
            ResolveRecordProtocols(record: record);
        }

        // Validate generic constraints reference declared type parameters
        _sa.ValidateConstraintTypeParameters(constraints: record.GenericConstraints,
            typeParameters: record.GenericParameters,
            location: record.Location);

        // Collect member variables and other members
        var memberVariables = new List<MemberVariableInfo>();
        var expandTemplates = new List<MemberExpandTemplateInfo>();
        int memberVariableIndex = 0;

        foreach (SyntaxTree.Declaration member in record.Members)
        {
            if (member is ExpandMemberDeclaration expandDecl)
            {
                expandTemplates.AddRange(collection: ResolveExpandTemplates(expandDecl: expandDecl));
                continue;
            }

            if (member is VariableDeclaration memberVariable)
            {
                memberVariables.Add(item: ResolveRecordMemberVariable(
                    memberVariable: memberVariable, memberVariableIndex: memberVariableIndex++));
            }

            // Still call CollectDeclaration for validation and other member types
            _sa.CollectDeclaration(node: member);
        }

        // Update the record with resolved member variables
        if (memberVariables.Count > 0)
        {
            _sa._registry.UpdateRecordMemberVariables(recordName: _sa._currentType!.FullName,
                memberVariables: memberVariables);
        }

        // Stash decl-position expand column templates on the generic definition; the registry
        // materializes them into concrete columns per source-type field at instantiation.
        if (expandTemplates.Count > 0 && _sa._currentType is RecordTypeInfo recordDef)
        {
            recordDef.ExpandTemplates = expandTemplates;
        }

        _sa._currentType = previousType;
        _sa._currentTypeMemberVariableNames = previousMemberVariableNames;
    }

    /// <summary>
    /// Resolves the <c>obeys</c> protocol list on a record declaration, reporting non-protocols and
    /// updating the registered record with the resolved protocols + conditional-obeys clause.
    /// </summary>
    private void ResolveRecordProtocols(RecordDeclaration record)
    {
        var resolvedProtocols = new List<TypeInfo>();
        foreach (TypeExpression protoExpr in record.Protocols)
        {
            TypeSymbol protoType = _typeResolver.ResolveType(typeExpr: protoExpr);
            if (protoType is ProtocolTypeInfo proto)
            {
                resolvedProtocols.Add(item: proto);
            }
            else if (protoType is not ErrorTypeInfo)
            {
                _sa.ReportError(code: SemanticDiagnosticCode.NotAProtocol,
                    message:
                    $"'{protoExpr.Name}' is not a protocol. Only protocols can be used with 'obeys'.",
                    location: protoExpr.Location);
            }
        }

        // Update the type with resolved protocols
        _sa._registry.UpdateRecordProtocols(recordName: _sa._currentType!.FullName,
            protocols: resolvedProtocols);
        if (_sa._currentType is RecordTypeInfo recTi)
            recTi.ConditionalObeys =
                Compiler.Declaration.StdlibLoader.BuildConditionalObeys(protoExprs: record.Protocols);
    }

    /// <summary>
    /// Resolves a single record member variable: resolves its type, validates it is a legal record
    /// field type (value type, entity/crashable reference, generic param, routine, or Assignable
    /// wrapper), and builds the <see cref="MemberVariableInfo"/>.
    /// </summary>
    private MemberVariableInfo ResolveRecordMemberVariable(VariableDeclaration memberVariable,
        int memberVariableIndex)
    {
        // Resolve member variable type
        TypeSymbol memberVariableType = memberVariable.Type != null
            ? _typeResolver.ResolveType(typeExpr: memberVariable.Type)
            : ErrorTypeInfo.Instance;

        // Records can contain: value types, entity/crashable REFERENCE fields (entities are
        // pointer-shaped reference types, so the field stores a reference), generic parameters,
        // and Assignable wrappers (Hijacked, Retained, Guarded, Tracked, Witnessed). Scoped access
        // tokens (Viewing, Modifying, Consulting, Amending) are wrappers NOT in the Assignable set.
        bool isReferenceTyped =
            memberVariableType?.Category == TypeCategory.Entity ||
            memberVariableType?.Category == TypeCategory.Crashable;
        // A Routine-typed field is a callback slot: the routine VALUE is pointer-shaped
        // (a `ptr` to a closure blob = C's `(fnptr[, userdata])`), stored NON-OWNING like a
        // bare C function pointer. So a record may hold one, mirroring how a C struct stores a
        // `(callback, userdata)` pair.
        bool isRoutineTyped = memberVariableType is RoutineTypeInfo;
        if (memberVariableType != null &&
            memberVariableType is not ErrorTypeInfo &&
            memberVariableType is not GenericParameterTypeInfo &&
            !TypeRegistry.IsValueType(type: memberVariableType) &&
            !isReferenceTyped &&
            !isRoutineTyped &&
            !(memberVariableType is WrapperTypeInfo wrapper &&
              AssignableWrapperTypes.Contains(item: wrapper.BareName)))
        {
            _sa.ReportError(code: SemanticDiagnosticCode.RecordContainsNonValueType,
                message:
                $"Record member variable '{memberVariable.Name}' has type '{memberVariableType.Name}' which is not a value type. " +
                "Records can only contain value types, Hijacked[T], and RC wrappers (Retained, Guarded, Tracked, Witnessed).",
                location: memberVariable.Location);
        }

        // Create member variable info
        return new MemberVariableInfo(name: memberVariable.Name, type: memberVariableType ?? ErrorTypeInfo.Instance)
        {
            Visibility = memberVariable.Visibility,
            Index = memberVariableIndex,
            HasDefaultValue = memberVariable.Initializer != null,
            Location = memberVariable.Location,
            Owner = _sa._currentType
        };
    }

    /// <summary>
    /// Resolves a decl-position <c>expand</c> directive into per-template
    /// <see cref="MemberExpandTemplateInfo"/>. Each column type is resolved with the <c>${m.type}</c>
    /// splice standing in for the synthetic per-field placeholder; the registry substitutes the real
    /// field type at instantiation.
    /// </summary>
    private List<MemberExpandTemplateInfo> ResolveExpandTemplates(ExpandMemberDeclaration expandDecl)
    {
        var result = new List<MemberExpandTemplateInfo>();
        foreach (ExpandMemberTemplate template in expandDecl.Templates)
        {
            TypeSymbol columnType = _typeResolver.ResolveType(typeExpr: template.Type);
            result.Add(item: new MemberExpandTemplateInfo(
                namePrefix: template.NamePrefix,
                sourceParamName: expandDecl.SourceType.Name,
                columnTypeTemplate: columnType,
                visibility: template.Visibility));
        }
        return result;
    }

    private void ResolveEntityBody(EntityDeclaration entity)
    {
        if (entity.Members.Count == 0 && !entity.HasPassBody)
        {
            _sa.ReportError(code: SemanticDiagnosticCode.EmptyBlockWithoutPass,
                message: "Empty entity body requires 'pass' keyword.",
                location: entity.Location);
        }

        TypeSymbol? previousType = _sa._currentType;
        HashSet<string>? previousMemberVariableNames = _sa._currentTypeMemberVariableNames;

        _sa._currentType = LookupTypeInCurrentModule(name: entity.Name);
        _sa._currentTypeMemberVariableNames = [];

        // Resolve implemented protocols
        if (_sa._currentType is EntityTypeInfo && entity.Protocols.Count > 0)
        {
            ResolveEntityProtocols(entity: entity);
        }

        // Collect member variables and other members
        var memberVariables = new List<MemberVariableInfo>();
        var expandTemplates = new List<MemberExpandTemplateInfo>();
        int memberVariableIndex = 0;

        foreach (SyntaxTree.Declaration member in entity.Members)
        {
            if (member is ExpandMemberDeclaration expandDecl)
            {
                expandTemplates.AddRange(collection: ResolveExpandTemplates(expandDecl: expandDecl));
                continue;
            }

            if (member is VariableDeclaration memberVariable)
            {
                memberVariables.Add(item: ResolveEntityMemberVariable(
                    memberVariable: memberVariable, memberVariableIndex: memberVariableIndex++));
            }

            _sa.CollectDeclaration(node: member);
        }

        if (memberVariables.Count > 0)
        {
            _sa._registry.UpdateEntityMemberVariables(entityName: _sa._currentType!.FullName,
                memberVariables: memberVariables);
        }

        if (expandTemplates.Count > 0 && _sa._currentType is EntityTypeInfo entityDef)
        {
            entityDef.ExpandTemplates = expandTemplates;
        }

        _sa._currentType = previousType;
        _sa._currentTypeMemberVariableNames = previousMemberVariableNames;
    }

    /// <summary>
    /// Resolves the <c>obeys</c> protocol list on an entity declaration, reporting non-protocols and
    /// updating the registered entity with the resolved protocols + conditional-obeys clause.
    /// </summary>
    private void ResolveEntityProtocols(EntityDeclaration entity)
    {
        var resolvedProtocols = new List<TypeInfo>();
        foreach (TypeExpression protoExpr in entity.Protocols)
        {
            TypeSymbol protoType = _typeResolver.ResolveType(typeExpr: protoExpr);
            if (protoType is ProtocolTypeInfo proto)
            {
                resolvedProtocols.Add(item: proto);
            }
            else if (protoType is not ErrorTypeInfo)
            {
                _sa.ReportError(code: SemanticDiagnosticCode.NotAProtocol,
                    message:
                    $"'{protoExpr.Name}' is not a protocol. Only protocols can be used with 'obeys'.",
                    location: protoExpr.Location);
            }
        }

        _sa._registry.UpdateEntityProtocols(entityName: _sa._currentType!.FullName,
            protocols: resolvedProtocols);
        if (_sa._currentType is EntityTypeInfo entTi)
            entTi.ConditionalObeys =
                Compiler.Declaration.StdlibLoader.BuildConditionalObeys(protoExprs: entity.Protocols);
    }

    /// <summary>
    /// Resolves a single entity member variable: resolves its (already-Roamed) type and records
    /// Suflae nullability as a flow fact from the <c>E?</c>-desugared <c>Maybe[...]</c> type expr.
    /// </summary>
    private MemberVariableInfo ResolveEntityMemberVariable(VariableDeclaration memberVariable,
        int memberVariableIndex)
    {
        TypeSymbol memberVariableType = memberVariable.Type != null
            ? _typeResolver.ResolveType(typeExpr: memberVariable.Type)
            : ErrorTypeInfo.Instance;

        // Suflae: an entity-typed field is a `Roamed[E]` biased-RC handle. The substitution now
        // happens at the single ResolveType choke point (TypeResolver.RoamSuflaeEntitySlot), so
        // memberVariableType is ALREADY `Roamed[E]` here (bare `x: E`) or a nullable `Roamed[E]`
        // (`x: E?` — the choke point collapses `Maybe[E]` to a bare nullable Roamed, since an
        // entity reference carries its own none via a null handle). We only still record
        // NULLABILITY as a flow fact: it is no longer visible in the resolved type, so detect it
        // from the AST — the field was written `E?`, which desugars to a `Maybe[...]` type expr.
        bool fieldNullable = _sa._registry.Language == Language.Suflae
            && memberVariable.Type is { Name: "Maybe" }
            && memberVariableType is RecordTypeInfo
                { GenericDefinition.Name: RuntimeContract.Roamed };

        return new MemberVariableInfo(name: memberVariable.Name, type: memberVariableType)
        {
            Visibility = memberVariable.Visibility,
            Index = memberVariableIndex,
            HasDefaultValue = memberVariable.Initializer != null,
            IsNullable = fieldNullable,
            Location = memberVariable.Location,
            Owner = _sa._currentType
        };
    }

    private void ResolveCrashableBody(CrashableDeclaration crashable)
    {
        TypeSymbol? previousType = _sa._currentType;
        HashSet<string>? previousMemberVariableNames = _sa._currentTypeMemberVariableNames;

        _sa._currentType = LookupTypeInCurrentModule(name: crashable.Name);
        _sa._currentTypeMemberVariableNames = [];

        var memberVariables = new List<MemberVariableInfo>();
        int memberVariableIndex = 0;

        foreach (SyntaxTree.Declaration member in crashable.Members)
        {
            if (member is VariableDeclaration memberVariable)
            {
                TypeSymbol memberVariableType = memberVariable.Type != null
                    ? _typeResolver.ResolveType(typeExpr: memberVariable.Type)
                    : ErrorTypeInfo.Instance;

                var memberVariableInfo =
                    new MemberVariableInfo(name: memberVariable.Name, type: memberVariableType)
                    {
                        Visibility = memberVariable.Visibility,
                        Index = memberVariableIndex++,
                        HasDefaultValue = memberVariable.Initializer != null,
                        Location = memberVariable.Location,
                        Owner = _sa._currentType
                    };
                memberVariables.Add(item: memberVariableInfo);
            }

            _sa.CollectDeclaration(node: member);
        }

        if (memberVariables.Count > 0)
        {
            _sa._registry.UpdateCrashableMemberVariables(typeName: _sa._currentType!.FullName,
                memberVariables: memberVariables);
        }

        _sa._currentType = previousType;
        _sa._currentTypeMemberVariableNames = previousMemberVariableNames;
    }

    private void ResolveProtocolBody(ProtocolDeclaration protocol)
    {
        // Look up the registered protocol type
        TypeSymbol? protoType = LookupTypeInCurrentModule(name: protocol.Name);
        if (protoType is not ProtocolTypeInfo protocolInfo)
        {
            return;
        }

        // Set current type context so generic parameters (T, U, etc.) are recognized
        TypeSymbol? previousType = _sa._currentType;
        _sa._currentType = protocolInfo;

        // Resolve parent protocols (protocol X obeys Y, Z)
        List<ProtocolTypeInfo> parentProtocols = ResolveParentProtocols(protocol: protocol);

        // Convert memberRoutine signatures to ProtocolMemberRoutineInfo
        var memberRoutines = new List<ProtocolMemberRoutineInfo>();
        foreach (RoutineSignature sig in protocol.MemberRoutines)
        {
            memberRoutines.Add(item: ConvertProtocolMemberRoutine(sig: sig));
        }

        // Update the protocol with resolved memberRoutines and parent protocols
        var updatedProtocol = new ProtocolTypeInfo(name: protocol.Name)
        {
            MemberRoutines = memberRoutines,
            ParentProtocols = parentProtocols,
            GenericParameters = protocolInfo.GenericParameters,
            GenericConstraints = protocolInfo.GenericConstraints,
            Visibility = protocolInfo.Visibility,
            Location = protocolInfo.Location,
            Module = protocolInfo.Module
        };

        // Replace the protocol in the registry
        _sa._registry.UpdateType(oldType: protocolInfo, newType: updatedProtocol);

        _sa._currentType = previousType;
    }

    /// <summary>
    /// Resolves a protocol's parent protocols (<c>protocol X obeys Y, Z</c>), reporting non-protocols.
    /// </summary>
    private List<ProtocolTypeInfo> ResolveParentProtocols(ProtocolDeclaration protocol)
    {
        var parentProtocols = new List<ProtocolTypeInfo>();
        foreach (TypeExpression parentExpr in protocol.ParentProtocols)
        {
            TypeSymbol parentType = _typeResolver.ResolveType(typeExpr: parentExpr);
            if (parentType is ProtocolTypeInfo parentProtocol)
            {
                parentProtocols.Add(item: parentProtocol);
            }
            else if (parentType is not ErrorTypeInfo)
            {
                _sa.ReportError(code: SemanticDiagnosticCode.NotAProtocol,
                    message:
                    $"'{parentExpr}' is not a protocol. Only protocols can be inherited with 'obeys'.",
                    location: parentExpr.Location);
            }
        }

        return parentProtocols;
    }

    /// <summary>
    /// Converts a single protocol member-routine signature to a <see cref="ProtocolMemberRoutineInfo"/>:
    /// classifies instance/type-level (Me. prefix vs common), resolves param + return types (skipping
    /// implicit <c>me</c>), and extracts mutation + generation-kind annotations.
    /// </summary>
    private ProtocolMemberRoutineInfo ConvertProtocolMemberRoutine(RoutineSignature sig)
    {
        bool isFailable = sig.IsFailable;
        string fullName = sig.Name;

        // Check if this is an instance memberRoutine (has "Me." prefix).
        // Protocol memberRoutines: "Me.MemberRoutineName" = instance, "memberRoutineName" = type-level.
        // The `common` qualifier overrides this — `common routine Me.identity()` is a
        // type-level memberRoutine even with the `Me.` prefix, matching the impl-side syntax
        // `common routine NumericSumAdd[T].identity() -> T`.
        bool isCommonMemberRoutine = sig.Annotations?.Contains(item: "common") == true;
        bool hasMePrefix = fullName.StartsWith(value: "Me.");
        bool isInstanceMemberRoutine = hasMePrefix && !isCommonMemberRoutine;
        string memberRoutineName = hasMePrefix
            ? fullName[3..]
            : fullName;

        // Resolve parameter types (skip 'me' if it appears as explicit parameter)
        var paramTypes = new List<TypeSymbol>();
        var paramNames = new List<string>();
        foreach (Parameter param in sig.Parameters)
        {
            // Skip the 'me' parameter - it's implicit for instance memberRoutines
            if (param.Name == "me")
            {
                continue;
            }

            TypeSymbol paramType = _typeResolver.ResolveProtocolType(typeExpr: param.Type);
            paramTypes.Add(item: paramType);
            paramNames.Add(item: param.Name);
        }

        // Resolve return type
        TypeSymbol? returnType = sig.ReturnType != null
            ? _typeResolver.ResolveProtocolType(typeExpr: sig.ReturnType)
            : null;

        // Extract mutation category from attributes (@readonly/@reshaping), via the one shared
        // derivation so this path can't drift from SignatureResolver / StdlibLoader.
        MutationCategory modification =
            MutationCategoryExtensions.FromAnnotations(annotations: sig.Annotations);

        // Extract generation kind from annotations
        ProtocolRoutineKind generationKind = ProtocolRoutineKind.None;
        if (sig.Annotations?.Contains(item: "innate") == true)
        {
            generationKind = ProtocolRoutineKind.Innate;
        }
        else if (sig.Annotations?.Contains(item: "generated") == true)
        {
            generationKind = ProtocolRoutineKind.Generated;
        }

        return new ProtocolMemberRoutineInfo(name: memberRoutineName)
        {
            IsInstanceMemberRoutine = isInstanceMemberRoutine,
            Mutation = modification,
            GenerationKind = generationKind,
            ParameterTypes = paramTypes,
            ParameterNames = paramNames,
            ReturnType = returnType,
            IsFailable = isFailable,
            HasDefaultImplementation = false, // Abstract protocol memberRoutines have no default
            Location = sig.Location
        };
    }

    private void ResolveVariantBody(VariantDeclaration variant)
    {
        if (variant.Members.Count == 0)
        {
            _sa.ReportError(code: SemanticDiagnosticCode.EmptyEnumerationBody,
                message: $"Variant type '{variant.Name}' must have at least one member.",
                location: variant.Location);
            return;
        }

        // Resolve each member type and build VariantMemberInfo list
        var members = new List<VariantMemberInfo>();
        var seenTypeNames = new HashSet<string>();
        bool hasNone = false;

        foreach (VariantMember member in variant.Members)
        {
            ResolveVariantMember(variant: variant, member: member, members: members,
                seenTypeNames: seenTypeNames, hasNone: ref hasNone);
        }

        foreach (VariantMemberInfo m in members)
        {
            if (!m.IsNone)
            {
                // Use reflection-free approach: find and update tag
                // Members are mutable via init, so we need to rebuild
            }
        }

        // Rebuild with correct tags
        var finalMembers = new List<VariantMemberInfo>();
        int tag = 0;
        // None first (tag 0) if present
        VariantMemberInfo? noneMember = members.FirstOrDefault(predicate: m => m.IsNone);
        if (noneMember != null)
        {
            finalMembers.Add(item: noneMember); // already tag 0
            tag = 1;
        }

        // Then all other members
        foreach (VariantMemberInfo m in members.Where(predicate: m => !m.IsNone))
        {
            finalMembers.Add(item: new VariantMemberInfo(type: m.Type!)
            {
                Ordinal = tag++, Location = m.Location
            });
        }

        // Update the registered type with resolved members
        if (LookupTypeInCurrentModule(name: variant.Name) is VariantTypeInfo variantType)
        {
            var updated = new VariantTypeInfo(name: variant.Name)
            {
                Members = finalMembers,
                GenericParameters = variant.GenericParameters,
                GenericConstraints = variant.GenericConstraints,
                Visibility = variantType.Visibility,
                Location = variant.Location,
                Module = variantType.Module
            };
            _sa._registry.UpdateType(oldType: variantType, newType: updated);
        }
    }

    /// <summary>
    /// Resolves a single variant member into the accumulating <paramref name="members"/> list: handles
    /// the zero-sized None case (dedup + tag-0 insert), rejects duplicate payload types, validates that
    /// tokens and Result/Lookup carriers are not used as payloads.
    /// </summary>
    private void ResolveVariantMember(VariantDeclaration variant, VariantMember member,
        List<VariantMemberInfo> members, HashSet<string> seenTypeNames, ref bool hasNone)
    {
        string typeName = member.Type.Name;

        // Handle None state (zero-sized, no payload)
        if (typeName == "None")
        {
            if (hasNone)
            {
                _sa.ReportError(code: SemanticDiagnosticCode.VariantCaseContainsInvalidType,
                    message: $"Variant type '{variant.Name}' has duplicate 'None' member.",
                    location: member.Location);
                return;
            }

            hasNone = true;
            // None is always tag 0
            members.Insert(index: 0,
                item: VariantMemberInfo.CreateNone(ordinal: 0, location: member.Location));
            return;
        }

        TypeSymbol memberType = _typeResolver.ResolveType(typeExpr: member.Type);

        // Check for duplicate types
        string memberTypeName = memberType.Name;
        if (!seenTypeNames.Add(item: memberTypeName))
        {
            _sa.ReportError(code: SemanticDiagnosticCode.VariantCaseContainsInvalidType,
                message:
                $"Variant type '{variant.Name}' has duplicate member type '{memberTypeName}'.",
                location: member.Location);
            return;
        }

        // Validate that tokens cannot be used as variant members
        _sa.ValidateNotTokenVariantPayload(type: memberType,
            caseName: memberTypeName,
            location: member.Location);

        // #59: Variant members cannot hold Result[T] or Lookup[T]. Nested variants are allowed.
        if (IsCarrierType(type: memberType) && !IsMaybeType(type: memberType))
        {
            _sa.ReportError(code: SemanticDiagnosticCode.VariantCaseContainsInvalidType,
                message: $"Variant member '{memberTypeName}' cannot be '{memberType.Name}'. " +
                         "Use failable routines (!) instead of storing Result/Lookup in variants.",
                location: member.Location);
        }
        // Post-Owned-retirement: bare entity T IS the bound/lvalue form (record-shaped
        // pointer), so a variant member of type T owns the bound entity directly.
        // Variant copyability derives from all members being Assignable.

        members.Add(item: new VariantMemberInfo(type: memberType)
        {
            Location = member.Location
        });
    }

    /// <summary>
    /// Resolves choice body, populating the choice cases.
    /// </summary>
    private void ResolveChoiceBody(ChoiceDeclaration choice)
    {
        if (choice.Cases.Count == 0)
        {
            _sa.ReportError(code: SemanticDiagnosticCode.EmptyEnumerationBody,
                message: $"Choice type '{choice.Name}' must have at least one case.",
                location: choice.Location);
            return;
        }

        TypeSymbol? choiceType = LookupTypeInCurrentModule(name: choice.Name);
        if (choiceType is not ChoiceTypeInfo choiceInfo)
        {
            return;
        }

        var cases = new List<ChoiceCaseInfo>();
        int autoValue = 0;

        foreach (ChoiceCase caseDecl in choice.Cases)
        {
            cases.Add(item: ResolveChoiceCase(choice: choice, caseDecl: caseDecl,
                autoValue: ref autoValue));
        }

        // Validate all-or-nothing explicit values
        int explicitCount = choice.Cases.Count(predicate: c => c.Value != null);
        if (explicitCount > 0 && explicitCount < choice.Cases.Count)
        {
            _sa.ReportError(code: SemanticDiagnosticCode.ChoiceMixedValues,
                message: $"Choice '{choice.Name}' mixes explicit and implicit case values. " +
                         "Either all cases must have explicit values or none should.",
                location: choice.Location);
        }

        // Validate no duplicate computed values
        var seenValues = new Dictionary<long, string>();
        foreach (ChoiceCaseInfo caseInfo in cases)
        {
            if (seenValues.TryGetValue(key: caseInfo.ComputedValue,
                    value: out string? existingCase))
            {
                _sa.ReportError(code: SemanticDiagnosticCode.ChoiceDuplicateValue,
                    message:
                    $"Choice '{choice.Name}' case '{caseInfo.Name}' has the same value ({caseInfo.ComputedValue}) as case '{existingCase}'.",
                    location: caseInfo.Location ?? choice.Location);
            }
            else
            {
                seenValues[key: caseInfo.ComputedValue] = caseInfo.Name;
            }
        }

        // Update the choice with resolved cases
        _sa._registry.UpdateChoiceCases(choiceName: choiceInfo.FullName, cases: cases);
    }

    /// <summary>
    /// Resolves a single choice case's value: evaluates an explicit value (checking S32 range) or
    /// assigns the running auto-increment value (checking overflow), advancing <paramref name="autoValue"/>.
    /// </summary>
    private ChoiceCaseInfo ResolveChoiceCase(ChoiceDeclaration choice, ChoiceCase caseDecl,
        ref int autoValue)
    {
        int? explicitValue = null;

        // Evaluate explicit value if provided
        if (caseDecl.Value != null)
        {
            long? longValue = TryEvaluateChoiceCaseValue(expression: caseDecl.Value,
                choice: choice,
                caseName: caseDecl.Name,
                location: caseDecl.Location);
            if (longValue.HasValue)
            {
                if (longValue.Value is < int.MinValue or > int.MaxValue)
                {
                    _sa.ReportError(code: SemanticDiagnosticCode.ChoiceCaseValueOverflow,
                        message:
                        $"Choice '{choice.Name}' case '{caseDecl.Name}': explicit value {longValue.Value} exceeds S32 range.",
                        location: caseDecl.Location);
                }
                else
                {
                    explicitValue = (int)longValue.Value;
                }
            }

            if (explicitValue.HasValue)
            {
                autoValue = explicitValue.Value;
                // Check auto-increment overflow
                if (autoValue == int.MaxValue)
                {
                    // Next auto-increment would overflow; only report if there are more cases after this
                    // The overflow will be caught when the next case tries to use autoValue + 1
                }
                else
                {
                    autoValue += 1;
                }
            }
        }

        int computedValue;
        if (explicitValue.HasValue)
        {
            computedValue = explicitValue.Value;
        }
        else
        {
            computedValue = autoValue;
            if (autoValue == int.MaxValue)
            {
                _sa.ReportError(code: SemanticDiagnosticCode.ChoiceCaseValueOverflow,
                    message:
                    $"Choice '{choice.Name}' case '{caseDecl.Name}': auto-assigned value would overflow S32 range.",
                    location: caseDecl.Location);
            }
            else
            {
                autoValue += 1;
            }
        }

        return new ChoiceCaseInfo(name: caseDecl.Name)
        {
            Value = explicitValue,
            ComputedValue = computedValue,
            Location = caseDecl.Location
        };
    }

    private void ResolveFlagsBody(FlagsDeclaration flags)
    {
        if (flags.Members.Count == 0)
        {
            _sa.ReportError(code: SemanticDiagnosticCode.EmptyEnumerationBody,
                message: $"Flags type '{flags.Name}' must have at least one member.",
                location: flags.Location);
            return;
        }

        TypeSymbol? flagsType = LookupTypeInCurrentModule(name: flags.Name);
        if (flagsType is not FlagsTypeInfo flagsInfo)
        {
            return;
        }

        // #127: Max 64 members (U64 backing)
        if (flags.Members.Count > 64)
        {
            _sa.ReportError(code: SemanticDiagnosticCode.FlagsTooManyMembers,
                message:
                $"Flags type '{flags.Name}' has {flags.Members.Count} members, but the maximum is 64.",
                location: flags.Location);
        }

        var members = new List<FlagsMemberInfo>();
        var seenNames = new HashSet<string>();

        for (int i = 0; i < flags.Members.Count; i++)
        {
            string memberName = flags.Members[index: i];

            // Validate no duplicate member names
            if (!seenNames.Add(item: memberName))
            {
                _sa.ReportError(code: SemanticDiagnosticCode.FlagsDuplicateMember,
                    message: $"Flags type '{flags.Name}' has duplicate member '{memberName}'.",
                    location: flags.Location);
                continue;
            }

            members.Add(item: new FlagsMemberInfo(Name: memberName, BitPosition: i));
        }

        _sa._registry.UpdateFlagsMembers(flagsName: flagsInfo.FullName, members: members);
    }

    /// <summary>
    /// Evaluates a choice case value expression to a long integer.
    /// Handles positive literals, negative unary expressions, and reports errors for invalid values.
    /// </summary>
    private long? TryEvaluateChoiceCaseValue(Expression expression, ChoiceDeclaration choice,
        string caseName, SourceLocation location)
    {
        // Positive integer literal
        if (expression is LiteralExpression literal)
        {
            return TryConvertLiteralToLong(value: literal.Value,
                choice: choice,
                caseName: caseName,
                location: location);
        }

        // Negative integer literal: -N
        if (expression is UnaryExpression
            {
                Operator: UnaryOperator.Minus, Operand: LiteralExpression negLiteral
            })
        {
            long? positiveValue = TryConvertLiteralToLong(value: negLiteral.Value,
                choice: choice,
                caseName: caseName,
                location: location);
            if (positiveValue.HasValue)
            {
                // Handle long.MinValue edge case: -(-9223372036854775808) can't be represented
                // But negating a positive value is fine for all values 0..long.MaxValue
                return -positiveValue.Value;
            }

            return null;
        }

        _sa.ReportError(code: SemanticDiagnosticCode.ChoiceCaseValueOverflow,
            message:
            $"Choice '{choice.Name}' case '{caseName}': value must be an integer literal.",
            location: location);
        return null;
    }

    /// <summary>
    /// Converts a literal object value to a long for choice case storage.
    /// The parser stores numeric literals as strings, so we parse them here.
    /// </summary>
    private long? TryConvertLiteralToLong(object value, ChoiceDeclaration choice, string caseName,
        SourceLocation location)
    {
        if (value is string strVal)
        {
            string cleaned = SemanticVerifier.CleanNumericLiteral(value: strVal);
            if (SemanticVerifier.TryParseSignedInteger(value: cleaned, result: out long result))
            {
                return result;
            }
        }

        return ReportChoiceValueError(choice: choice, caseName: caseName, location: location);
    }

    private long? ReportChoiceValueError(ChoiceDeclaration choice, string caseName,
        SourceLocation location)
    {
        _sa.ReportError(code: SemanticDiagnosticCode.ChoiceCaseValueOverflow,
            message:
            $"Choice '{choice.Name}' case '{caseName}': value must be an integer literal within S64 range.",
            location: location);
        return null;
    }

    #endregion

    // Static helpers

    private static string? GetCarrierBaseName(TypeSymbol type)
    {
        if (type is not RecordTypeInfo r)
        {
            return null;
        }

        string baseName = r.GenericDefinition?.Name ?? r.Name;
        return baseName is "Maybe" or "Result" or "Lookup" ? baseName : null;
    }

    private static bool IsCarrierType(TypeSymbol type) => GetCarrierBaseName(type: type) != null;

    private static bool IsMaybeType(TypeSymbol type) => GetCarrierBaseName(type: type) == "Maybe";

    private static readonly HashSet<string> AssignableWrapperTypes =
    [
        RuntimeContract.Hijacked, // Unmanaged raw pointer handle
        RuntimeContract.Retained, // Reference-counted handle
        RuntimeContract.Guarded,   // Reference-counted multi-threaded handle
        RuntimeContract.Tracked,  // Weak reference handle
        RuntimeContract.Witnessed,  // Weak reference multi-threaded handle
    ];
}
