using Compiler.Diagnostics;
using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;
using Compiler.Verification.Enums;

namespace Compiler.Verification;

using TypeSymbol = TypeInfo;

public sealed partial class SemanticVerifier
{
    private static TypeSymbol SubstituteTypeParams(TypeSymbol type,
        Dictionary<string, TypeSymbol> substitution)
    {
        // Direct substitution for generic parameters
        if (type is GenericParameterTypeInfo &&
            substitution.TryGetValue(key: type.Name, value: out TypeSymbol? sub))
        {
            return sub;
        }

        // Recursive substitution in type arguments
        if (type.TypeArguments is not { Count: > 0 })
        {
            return type;
        }

        var newArgs = type.TypeArguments
                          .Select(selector: arg =>
                               SubstituteTypeParams(type: arg, substitution: substitution))
                          .ToList();

        // Check if anything actually changed
        bool changed = false;
        for (int i = 0; i < newArgs.Count; i++)
        {
            if (!ReferenceEquals(objA: newArgs[index: i], objB: type.TypeArguments[index: i]))
            {
                changed = true;
                break;
            }
        }

        if (!changed)
        {
            return type;
        }

        // Get the generic definition and create a new instance with substituted args
        TypeSymbol? genericDef = type switch
        {
            RecordTypeInfo r => r.GenericDefinition,
            EntityTypeInfo e => e.GenericDefinition,
            ProtocolTypeInfo p => p.GenericDefinition,
            _ => null
        };

        if (genericDef != null)
        {
            return genericDef.CreateInstance(typeArguments: newArgs);
        }

        // TupleTypeInfo doesn't have a GenericDefinition — create a new tuple directly
        if (type is TupleTypeInfo)
        {
            return new TupleTypeInfo(elementTypes: newArgs);
        }

        return type;
    }

    /// <summary>
    /// Reports RF-S150 for each <c>needs &lt;param&gt; obeys P</c> constraint on a generic ROUTINE that the
    /// resolved explicit type arguments violate — the clean call-site diagnostic that replaces the
    /// over-prune codegen crash (the body's <c>x.duplicate()</c> etc. would otherwise be pruned when the
    /// arg fails the bound, surfacing as an "undefined symbol"). Uses the onlyif-aware
    /// <see cref="ImplementsProtocol"/> so a conditional-conformance failure (e.g. <c>List[NonCopyable]</c>
    /// vs <c>T obeys Copyable</c>) is caught. Args still abstract (a nested generic param) are skipped —
    /// they are validated once fully concrete.
    /// </summary>
    internal void ValidateRoutineGenericConstraints(RoutineInfo routine, List<TypeSymbol> typeArgs,
        SourceLocation location)
    {
        if (routine.GenericConstraints is not { Count: > 0 } constraints ||
            routine.GenericParameters is not { Count: > 0 } genParams)
            return;
        foreach (GenericConstraintDeclaration c in constraints)
        {
            if (c.ConstraintType != ConstraintKind.Obeys || c.ConstraintTypes == null)
                continue;
            int idx = genParams.IndexOf(item: c.ParameterName);
            if (idx < 0 || idx >= typeArgs.Count)
                continue;
            TypeSymbol arg = typeArgs[index: idx];
            if (arg is GenericParameterTypeInfo)
                continue;
            foreach (TypeExpression protoExpr in c.ConstraintTypes)
                if (!ImplementsProtocol(type: arg, protocolName: protoExpr.Name))
                    ReportError(code: SemanticDiagnosticCode.ProtocolConstraintViolation,
                        message: $"Type '{arg.Name}' does not implement protocol '{protoExpr.Name}' " +
                                 $"required by constraint on '{c.ParameterName}'.",
                        location: location);
        }
    }

    /// <summary>
    /// Reports RF-S150 when a resolved MEMBER routine's owner-level <c>needs &lt;param&gt; obeys P</c>
    /// constraints are violated by the concrete receiver's bound type args — e.g.
    /// <c>List[Widget].duplicate()</c> where <c>List.duplicate</c> carries <c>needs T obeys Copyable</c> but
    /// <c>Widget</c> is not Copyable. Without this the call resolves, a body-less concrete instance is
    /// referenced, and codegen trips the "declared+called but never defined" over-prune crash instead of a
    /// clean diagnostic. Permissive on anything unevaluable (non-generic receiver, missing def params).
    /// </summary>
    internal void ValidateMemberOwnerConstraints(RoutineInfo memberRoutine, TypeSymbol ownerType,
        SourceLocation location)
    {
        // Owner-level `needs param obeys P` can only exist on a GENERIC receiver; a non-generic receiver
        // (no bound type args) has nothing to check. Short-circuit here so the common member call never
        // pays the def-method lookup below — this runs on EVERY member call, so the guard is load-bearing
        // for compile speed (variant-body analysis touches thousands of member calls).
        if (ownerType.TypeArguments is not { Count: > 0 })
            return;
        // The owner's generic DEFINITION (List[T] for a List[Widget] receiver) carries the def params +
        // the method's `needs`; the resolved instance drops the constraints, so read them from the def.
        TypeSymbol? ownerDef =
            (ownerType as EntityTypeInfo)?.GenericDefinition
            ?? (ownerType as RecordTypeInfo)?.GenericDefinition as TypeSymbol;
        List<GenericConstraintDeclaration>? constraints = memberRoutine.GenericConstraints;
        if (constraints is not { Count: > 0 } && ownerDef != null)
            constraints = _registry.LookupMemberRoutine(type: ownerDef,
                memberRoutineName: memberRoutine.Name)?.GenericConstraints;
        if (constraints is not { Count: > 0 })
            return;
        List<string>? paramNames = (ownerDef ?? ownerType).GenericParameters ?? ownerType.GenericParameters;
        List<TypeSymbol>? args = ownerType.TypeArguments;
        if (paramNames is null || args is null)
            return;
        var subs = new Dictionary<string, TypeSymbol>(comparer: System.StringComparer.Ordinal);
        for (int i = 0; i < paramNames.Count && i < args.Count; i++)
            subs[key: paramNames[i]] = args[i];
        foreach (GenericConstraintDeclaration c in constraints)
        {
            if (c.ConstraintType != ConstraintKind.Obeys || c.ConstraintTypes == null)
                continue;
            if (!subs.TryGetValue(key: c.ParameterName, value: out TypeSymbol? actual)
                || actual is GenericParameterTypeInfo)
                continue;
            foreach (TypeExpression protoExpr in c.ConstraintTypes)
                if (!ImplementsProtocol(type: actual, protocolName: protoExpr.Name))
                    ReportError(code: SemanticDiagnosticCode.ProtocolConstraintViolation,
                        message: $"'{ownerType.Name}.{memberRoutine.Name}' requires '{c.ParameterName} " +
                                 $"obeys {protoExpr.Name}', but '{actual.Name}' does not.",
                        location: location);
        }
    }

    /// <summary>
    /// Returns true if <paramref name="type"/> implements the named protocol.
    /// Checks explicit protocol declarations, parent protocol chains, and structural conformance
    /// (i.e., whether the type has all required memberRoutines of the protocol).
    /// </summary>
    internal bool ImplementsProtocol(TypeSymbol type, string protocolName)
    {
        // Get the protocol type. Parameterised protocol names like "Controlling[List[S64]]"
        // won't resolve directly (only the generic definition is registered) — strip brackets
        // and fall back to the base name so conformance checks see the protocol either way.
        TypeSymbol? protocol = LookupTypeWithImports(name: protocolName);
        if (protocol is not { Category: TypeCategory.Protocol } &&
            protocolName.Contains(value: '['))
        {
            string baseProtocolName = BareTypeName(typeName: protocolName);
            protocol = LookupTypeWithImports(name: baseProtocolName);
        }
        if (protocol is not { Category: TypeCategory.Protocol })
        {
            return false;
        }

        // Type-category protocols are satisfied by category membership itself: a record obeys
        // RecordType by BEING a record — no declaration needed (their parent protocols like
        // Equatable/Hashable are auto-derived for these categories). Without this, a
        // `needs M obeys RecordType` constraint can only be met by the wrong-spelling
        // workaround `M is RecordType`.
        if (MatchesCategoryProtocol(type: type, protocolName: protocolName))
        {
            return true;
        }

        // Generic parameter: check current routine/owner type constraints for obeys declarations.
        // e.g., needs T obeys Equatable means T satisfies Equatable inside this routine's body.
        if (type is GenericParameterTypeInfo)
        {
            return GenericParamObeysConstraint(type: type, protocolName: protocolName);
        }

        // DECLARED conformance (own ImplementedProtocols + their parent chain, gated by `onlyif`, plus the
        // reflexive marker-protocol rule) is decided by the single registry authority — no duplicate walk
        // here. A positive result covers e.g. `List[NonCopyable]` NOT satisfying `T obeys Copyable`
        // (RF-S150), and Viewing/Modifying/value types satisfying Accessing/Controlling.
        if (_registry.TypeObeysProtocol(type: type, protocolName: protocolName))
        {
            return true;
        }

        // STRUCTURAL conformance (the type has all of the protocol's required member routines) is the
        // SA-only fallback the registry cannot compute.
        List<TypeSymbol>? implementedProtocols = type switch
        {
            RecordTypeInfo record => record.ImplementedProtocols,
            EntityTypeInfo entity => entity.ImplementedProtocols,
            _ => null
        };
        if (implementedProtocols != null && protocol is ProtocolTypeInfo protoType)
        {
            return ImplementsProtocolStructurally(type: type, protoType: protoType,
                protocolName: protocolName, implementedProtocols: implementedProtocols);
        }

        return false;
    }

    /// <summary>
    /// Returns true when <paramref name="protocolName"/> is a type-category protocol
    /// (RecordType/EntityType/…) that <paramref name="type"/> satisfies by category membership.
    /// Extracted from <see cref="ImplementsProtocol"/>.
    /// </summary>
    private static bool MatchesCategoryProtocol(TypeSymbol type, string protocolName)
    {
        return protocolName switch
        {
            "RecordType" => type.Category == TypeCategory.Record,
            "EntityType" => type.Category == TypeCategory.Entity,
            "ChoiceType" => type.Category == TypeCategory.Choice,
            "VariantType" => type.Category == TypeCategory.Variant,
            "FlagsType" => type.Category == TypeCategory.Flags,
            "Crashable" => type.Category == TypeCategory.Crashable,
            _ => false
        };
    }

    /// <summary>
    /// Returns true when the generic-parameter <paramref name="type"/> obeys
    /// <paramref name="protocolName"/> via an active <c>obeys</c> constraint on the current routine or
    /// its owner type. Extracted from <see cref="ImplementsProtocol"/>.
    /// </summary>
    private bool GenericParamObeysConstraint(TypeSymbol type, string protocolName)
    {
        if (_currentRoutine?.GenericConstraints != null &&
            _currentRoutine.GenericConstraints.Any(c =>
                c.ParameterName == type.Name && c is { ConstraintType: ConstraintKind.Obeys, ConstraintTypes: not null } &&
                c.ConstraintTypes.Any(ct => ct.Name == protocolName)))
            return true;

        TypeSymbol? ownerType = _currentRoutine?.OwnerType;
        if (ownerType?.GenericConstraints == null)
        {
            return false;
        }

        return ownerType.GenericConstraints.Any(c =>
            c.ParameterName == type.Name && c is { ConstraintType: ConstraintKind.Obeys, ConstraintTypes: not null } &&
            c.ConstraintTypes.Any(ct => ct.Name == protocolName));
    }

    /// <summary>
    /// Handles the structural-conformance tail of <see cref="ImplementsProtocol"/>: the implicit
    /// entity Accessing/Controlling satisfaction, the transparent readonly relay through a wrapper's
    /// inner type, and the final member-routine structural check.
    /// </summary>
    private bool ImplementsProtocolStructurally(TypeSymbol type, ProtocolTypeInfo protoType,
        string protocolName, List<TypeSymbol> implementedProtocols)
    {
        // Entity T implicitly satisfies Accessing[T] and Controlling[T]
        if (type.Category == TypeCategory.Entity &&
            protoType.TypeArguments is { Count: 1 } args &&
            args[index: 0].Name == type.Name)
        {
            string baseProto = (protoType.GenericDefinition ?? protoType).BareName;
            if (Compiler.Declaration.RuntimeContract.IsMarkerProtocol(baseName: baseProto))
            {
                return true;
            }
        }

        // Transparent relay for Accessing[T] / Controlling[T]:
        // A wrapper type satisfies any readonly protocol that its inner entity type satisfies.
        // All @readonly protocol memberRoutines are safe to delegate through both read-only
        // (Accessing) and read-write (Controlling) wrappers.
        if (IsAllReadOnlyProtocol(protoType))
        {
            TypeSymbol? innerT = GetReferringControllingInnerType(protocols: implementedProtocols);
            if (innerT != null && ImplementsProtocol(type: innerT, protocolName: protocolName))
                return true;
        }

        return CheckStructuralConformance(type: type, protocol: protoType);
    }

    /// <summary>
    /// Returns true if <paramref name="type"/> explicitly declares conformance to the named protocol
    /// via <c>obeys</c>. Unlike <see cref="ImplementsProtocol"/>, this does NOT fall back to
    /// structural conformance, making it suitable for marker protocols like ConstCompatible.
    /// </summary>
    internal bool ExplicitlyImplementsProtocol(TypeSymbol type, string protocolName)
    {
        List<TypeSymbol>? implementedProtocols = type switch
        {
            RecordTypeInfo record => record.ImplementedProtocols,
            EntityTypeInfo entity => entity.ImplementedProtocols,
            _ => null
        };

        if (implementedProtocols == null)
        {
            return false;
        }

        return implementedProtocols.Any(implemented =>
            implemented.Name == protocolName ||
            implemented.BareName == protocolName ||
            (implemented is ProtocolTypeInfo proto &&
             CheckParentProtocols(proto: proto, targetName: protocolName)));
    }

    /// <summary>
    /// Checks if any parent protocol matches the target.
    /// </summary>
    internal bool CheckParentProtocols(ProtocolTypeInfo proto, string targetName)
    {
        foreach (ProtocolTypeInfo parent in proto.ParentProtocols)
        {
            if (parent.Name == targetName || parent.BareName == targetName)
            {
                return true;
            }

            // Re-lookup parent from registry to get the latest version with populated ParentProtocols,
            // since immutable type updates may leave stale references in the hierarchy.
            ProtocolTypeInfo latestParent = parent;
            if (parent.ParentProtocols.Count == 0)
            {
                TypeSymbol? looked = _registry.LookupType(name: parent.Name);
                if (looked is ProtocolTypeInfo latest)
                {
                    latestParent = latest;
                }
            }

            if (CheckParentProtocols(proto: latestParent, targetName: targetName))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Checks if a type structurally conforms to a protocol by having all required memberRoutines.
    /// </summary>
    private bool CheckStructuralConformance(TypeSymbol type, ProtocolTypeInfo protocol)
    {
        // Marker protocols (no memberRoutines) require explicit conformance — never structurally satisfied
        if (protocol.MemberRoutines.Count == 0)
        {
            return false;
        }

        foreach (ProtocolMemberRoutineInfo requiredMemberRoutine in protocol.MemberRoutines)
        {
            // Skip memberRoutines with default implementations
            if (requiredMemberRoutine.HasDefaultImplementation)
            {
                continue;
            }

            // Look for the memberRoutine on the type
            RoutineInfo? typeMemberRoutine =
                _registry.LookupMemberRoutine(type: type, memberRoutineName: requiredMemberRoutine.Name);
            if (typeMemberRoutine == null)
            {
                // memberRoutine names are bare; the failable `!` is a structured flag. Retry matching a
                // same-named failable implementation via the isFailable filter.
                if (requiredMemberRoutine.IsFailable)
                {
                    typeMemberRoutine = _registry.LookupMemberRoutine(type: type,
                        memberRoutineName: requiredMemberRoutine.Name, isFailable: true);
                }

                if (typeMemberRoutine == null)
                {
                    return false;
                }
            }

            // Verify memberRoutine signature matches (basic check)
            if (!memberRoutineSignatureMatches(typeMemberRoutine: typeMemberRoutine, protoMemberRoutine: requiredMemberRoutine))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Checks if a type's memberRoutine signature matches a protocol memberRoutine signature.
    /// </summary>
    private bool memberRoutineSignatureMatches(RoutineInfo typeMemberRoutine, ProtocolMemberRoutineInfo protoMemberRoutine)
    {
        // Check failable matches
        if (typeMemberRoutine.IsFailable != protoMemberRoutine.IsFailable)
        {
            return false;
        }

        // Check parameter count (excluding 'me' parameter if present)
        // In-body memberRoutines have explicit 'me' as first parameter
        // Extension memberRoutines don't include 'me' in the parameter list
        int expectedParamCount = protoMemberRoutine.ParameterTypes.Count;
        bool hasMeParam = typeMemberRoutine.Parameters.Count > 0 &&
                          typeMemberRoutine.Parameters[index: 0].Name == "me";
        int actualParamCount = typeMemberRoutine.Parameters.Count - (hasMeParam
            ? 1
            : 0);

        if (actualParamCount != expectedParamCount)
        {
            return false;
        }

        if (!MemberRoutineParameterTypesMatch(typeMemberRoutine: typeMemberRoutine,
                protoMemberRoutine: protoMemberRoutine, expectedParamCount: expectedParamCount,
                hasMeParam: hasMeParam))
        {
            return false;
        }

        return MemberRoutineReturnTypeMatches(typeMemberRoutine: typeMemberRoutine,
            protoMemberRoutine: protoMemberRoutine);
    }

    /// <summary>
    /// Checks that each of the type memberRoutine's parameter types (skipping 'me' when present)
    /// matches the corresponding protocol parameter type. Extracted from
    /// <see cref="memberRoutineSignatureMatches"/>.
    /// </summary>
    private bool MemberRoutineParameterTypesMatch(RoutineInfo typeMemberRoutine,
        ProtocolMemberRoutineInfo protoMemberRoutine, int expectedParamCount, bool hasMeParam)
    {
        // Check parameter types - skip 'me' if present
        int startIndex = hasMeParam
            ? 1
            : 0;
        for (int i = 0; i < expectedParamCount; i++)
        {
            TypeSymbol expectedType = protoMemberRoutine.ParameterTypes[index: i];
            TypeSymbol actualType = typeMemberRoutine.Parameters[index: startIndex + i].Type;

            // Handle protocol self type (Me) - should match the implementing type
            if (expectedType is ProtocolSelfTypeInfo)
            {
                // 'Me' in protocol should match the owner type of the memberRoutine
                if (typeMemberRoutine.OwnerType != null &&
                    !TypesMatch(actual: actualType, expected: typeMemberRoutine.OwnerType))
                {
                    return false;
                }
            }
            else if (!TypesMatch(actual: actualType, expected: expectedType))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Checks that the type memberRoutine's return type is compatible with the protocol
    /// memberRoutine's declared return type. Extracted from
    /// <see cref="memberRoutineSignatureMatches"/>.
    /// </summary>
    private bool MemberRoutineReturnTypeMatches(RoutineInfo typeMemberRoutine,
        ProtocolMemberRoutineInfo protoMemberRoutine)
    {
        // Check return type (if specified)
        if (protoMemberRoutine.ReturnType != null && typeMemberRoutine.ReturnType != null)
        {
            if (!IsAssignableTo(source: typeMemberRoutine.ReturnType, target: protoMemberRoutine.ReturnType))
            {
                return false;
            }
        }
        else if (protoMemberRoutine.ReturnType == null != (typeMemberRoutine.ReturnType == null))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Returns true if all required memberRoutines in <paramref name="protocol"/> and its parent chain
    /// have <see cref="MutationCategory.Readonly"/> mutation. Marker protocols with no
    /// memberRoutines return false — they require explicit declaration, not relay.
    /// </summary>
    private bool IsAllReadOnlyProtocol(ProtocolTypeInfo protocol)
    {
        if (protocol.MemberRoutines.Count == 0)
            return false;

        foreach (ProtocolMemberRoutineInfo memberRoutine in protocol.MemberRoutines)
        {
            if (memberRoutine.Mutation != MutationCategory.Readonly)
                return false;
        }

        foreach (ProtocolTypeInfo parent in protocol.ParentProtocols)
        {
            // Re-lookup to get a fully-populated parent (same pattern as CheckParentProtocols).
            ProtocolTypeInfo resolved = parent;
            if (_registry.LookupType(name: parent.Name) is ProtocolTypeInfo latest)
                resolved = latest;

            if (!IsAllReadOnlyProtocol(resolved))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Extracts the inner type T from the first <c>Accessing[T]</c> or <c>Controlling[T]</c>
    /// entry in <paramref name="protocols"/>. Returns null if neither is present.
    /// </summary>
    private static TypeSymbol? GetReferringControllingInnerType(List<TypeSymbol> protocols)
    {
        foreach (TypeSymbol proto in protocols)
        {
            string baseName = proto.BareName;
            if (Compiler.Declaration.RuntimeContract.IsMarkerProtocol(baseName: baseName) && proto.TypeArguments is { Count: 1 })
                return proto.TypeArguments[index: 0];
        }

        return null;
    }

    /// <summary>
    /// Checks if two types match for protocol signature comparison.
    /// </summary>
    private static bool TypesMatch(TypeSymbol actual, TypeSymbol expected)
    {
        // Exact name match
        if (actual.Name == expected.Name)
        {
            return true;
        }

        // Handle ProtocolSelfTypeInfo in expected position
        if (expected is ProtocolSelfTypeInfo)
        {
            // 'Me' matches the owner type - handled by caller
            return true;
        }

        // Handle generic resolutions
        if (expected.IsGenericDefinition && actual.IsGenericResolution)
        {
            string baseName = actual.BareName;
            if (baseName == expected.Name)
            {
                return true;
            }
        }

        return false;
    }
}
