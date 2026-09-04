using System;
using System.Collections.Generic;
using System.Linq;
using Compiler.Resolution;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;
using Verification.Enums;

namespace Verification;

using TypeSymbol = TypeInfo;

/// <summary>
/// Central authority for BuilderQuery routine registration and import-gating.
/// Provides static name sets for identifying BS routines and memberRoutines to register
/// them on types, as standalone functions, and as module-level routines.
/// </summary>
public static class BuilderInfoProvider
{
    /// <summary>Per-type BuilderQuery member routines (require 'import BuilderQuery').</summary>
    private static readonly IReadOnlySet<string> PerTypeRoutines = RuntimeContract.BuilderPerTypeRoutines;

    /// <summary>Standalone BuilderQuery routines (require 'import BuilderQuery').</summary>
    private static readonly IReadOnlySet<string> StandaloneRoutines = RuntimeContract.BuilderStandaloneRoutines;

    /// <summary>Returns true if the routine name is a per-type BuilderQuery member routine.</summary>
    public static bool IsBuilderQueryRoutine(string name)
    {
        return PerTypeRoutines.Contains(item: name);
    }

    /// <summary>
    /// Per-type CONSTANT list-returning BuilderQuery reflection routines: 0 runtime params, value is a
    /// compile-time-constant <c>List[Text]</c> of the owner type. These are NOT synthesized as routine
    /// bodies (see <c>WiredRoutinePass.TryHandleBuilderQueryConstant</c>); they are folded at the call site
    /// to an inline analyzed list literal by <c>SemanticVerifier.FoldListBuilderQueryReflection</c> BEFORE
    /// reachability — the list analogue of the scalar <see cref="IsFoldable"/> foldables, so BuilderQuery
    /// never survives desugaring as an emitted routine (which the non-pruned resident-JIT base would emit
    /// dead, dangling an unmaterialized <c>from_literal(Array[Text,N])</c>).
    /// </summary>
    public static readonly IReadOnlySet<string> ListReturningConstantRoutines =
        new HashSet<string>(comparer: StringComparer.Ordinal)
        {
            "routine_names", "protocols", "generic_args", "annotations", "dependencies"
        };

    /// <summary>Returns true if the routine name is a constant list-returning BuilderQuery reflection routine.</summary>
    public static bool IsListReturningConstantRoutine(string name) =>
        ListReturningConstantRoutines.Contains(item: name);

    /// <summary>Returns true if the routine name is a standalone BuilderQuery routine.</summary>
    public static bool IsBuilderQueryStandalone(string name)
    {
        return StandaloneRoutines.Contains(item: name);
    }

    /// <summary>
    /// Registers all per-type BuilderQuery metadata routines on a given type.
    /// </summary>
    public static void RegisterRoutinesOnType(TypeSymbol type, List<RoutineInfo> existingMemberRoutines,
        TypeRegistry registry, TypeSymbol? textType, TypeSymbol? boolType,
        TypeSymbol? u64Type, TypeSymbol? s64Type, TypeSymbol? listTextType,
        TypeSymbol? listFieldInfoType, TypeSymbol? listProtocolInfoType,
        TypeSymbol? listRoutineInfoType,
        TypeSymbol? byteSizeType = null)
    {
        RegisterScalarReturningRoutines(type: type, existingMemberRoutines: existingMemberRoutines,
            registry: registry, textType: textType, boolType: boolType, u64Type: u64Type,
            s64Type: s64Type, byteSizeType: byteSizeType);

        RegisterListReturningRoutines(type: type, existingMemberRoutines: existingMemberRoutines,
            registry: registry, listTextType: listTextType, listFieldInfoType: listFieldInfoType,
            listProtocolInfoType: listProtocolInfoType, listRoutineInfoType: listRoutineInfoType);

        // member_type_id(member_name: Text) -> U64
        if (u64Type != null && textType != null)
        {
            MaybeRegisterWithParam(owner: type,
                name: "member_type_id",
                paramName: "member_name",
                paramType: textType,
                returnType: u64Type,
                existingMemberRoutines: existingMemberRoutines,
                registry: registry);
        }
    }

    /// <summary>
    /// Registers the per-type BuilderQuery routines whose return type is a scalar/choice
    /// (Text/U64/ByteSize/S64/TypeKind/Bool), gated on the availability of each carrier type.
    /// </summary>
    private static void RegisterScalarReturningRoutines(TypeSymbol type,
        List<RoutineInfo> existingMemberRoutines, TypeRegistry registry, TypeSymbol? textType,
        TypeSymbol? boolType, TypeSymbol? u64Type, TypeSymbol? s64Type, TypeSymbol? byteSizeType)
    {
        // Text-returning routines
        if (textType != null)
        {
            MaybeRegister(owner: type,
                name: "type_name",
                returnType: textType,
                existingMemberRoutines: existingMemberRoutines,
                registry: registry);
            MaybeRegister(owner: type,
                name: "module_name",
                returnType: textType,
                existingMemberRoutines: existingMemberRoutines,
                registry: registry);
            MaybeRegister(owner: type,
                name: "full_type_name",
                returnType: textType,
                existingMemberRoutines: existingMemberRoutines,
                registry: registry);
        }

        // U64-returning routines
        if (u64Type != null)
        {
            MaybeRegister(owner: type,
                name: "type_id",
                returnType: u64Type,
                existingMemberRoutines: existingMemberRoutines,
                registry: registry);
        }

        // data_size is RazorForge-only and should keep its declared ByteSize surface.
        if (registry.Language == Language.RazorForge && byteSizeType != null)
        {
            MaybeRegister(owner: type,
                name: RuntimeContract.DataSize,
                returnType: byteSizeType,
                existingMemberRoutines: existingMemberRoutines,
                registry: registry);
        }

        // S64-returning routines
        if (s64Type != null)
        {
            MaybeRegister(owner: type,
                name: "member_variable_count",
                returnType: s64Type,
                existingMemberRoutines: existingMemberRoutines,
                registry: registry);
        }

        // type_kind returns the declared TypeKind choice only (module-qualified: TypeKind lives in
        // `module BuilderQuery`, so a bare lookup depended on the cross-module short-name scan).
        TypeSymbol? typeKindType = registry.LookupType(name: "BuilderQuery.TypeKind");
        if (typeKindType != null)
        {
            MaybeRegister(owner: type,
                name: "type_kind",
                returnType: typeKindType,
                existingMemberRoutines: existingMemberRoutines,
                registry: registry);
        }

        // Bool-returning routines
        if (boolType != null)
        {
            MaybeRegister(owner: type,
                name: "is_generic",
                returnType: boolType,
                existingMemberRoutines: existingMemberRoutines,
                registry: registry);
            MaybeRegister(owner: type,
                name: "is_in_flight",
                returnType: boolType,
                existingMemberRoutines: existingMemberRoutines,
                registry: registry);
        }
    }

    /// <summary>
    /// Registers the per-type BuilderQuery routines whose return type is a list carrier
    /// (List[Text]/List[FieldInfo]/List[ProtocolInfo]/List[RoutineInfo]), gated on the
    /// availability of each carrier type.
    /// </summary>
    private static void RegisterListReturningRoutines(TypeSymbol type,
        List<RoutineInfo> existingMemberRoutines, TypeRegistry registry, TypeSymbol? listTextType,
        TypeSymbol? listFieldInfoType, TypeSymbol? listProtocolInfoType, TypeSymbol? listRoutineInfoType)
    {
        // List[Text]-returning routines
        if (listTextType != null)
        {
            MaybeRegister(owner: type,
                name: "protocols",
                returnType: listTextType,
                existingMemberRoutines: existingMemberRoutines,
                registry: registry);
            MaybeRegister(owner: type,
                name: "routine_names",
                returnType: listTextType,
                existingMemberRoutines: existingMemberRoutines,
                registry: registry);
            MaybeRegister(owner: type,
                name: "annotations",
                returnType: listTextType,
                existingMemberRoutines: existingMemberRoutines,
                registry: registry);
            MaybeRegister(owner: type,
                name: "generic_args",
                returnType: listTextType,
                existingMemberRoutines: existingMemberRoutines,
                registry: registry);
            MaybeRegister(owner: type,
                name: "dependencies",
                returnType: listTextType,
                existingMemberRoutines: existingMemberRoutines,
                registry: registry);
        }

        // List[FieldInfo]-returning routines
        if (listFieldInfoType != null)
        {
            MaybeRegister(owner: type,
                name: "member_variable_info",
                returnType: listFieldInfoType,
                existingMemberRoutines: existingMemberRoutines,
                registry: registry);
        }

        // List[ProtocolInfo]-returning routines
        if (listProtocolInfoType != null)
        {
            MaybeRegister(owner: type,
                name: "protocol_info",
                returnType: listProtocolInfoType,
                existingMemberRoutines: existingMemberRoutines,
                registry: registry);
        }

        // List[RoutineInfo]-returning routines
        if (listRoutineInfoType != null)
        {
            MaybeRegister(owner: type,
                name: "routine_info",
                returnType: listRoutineInfoType,
                existingMemberRoutines: existingMemberRoutines,
                registry: registry);
        }
    }

    /// <summary>
    /// Registers standalone BuilderQuery routines (source_*, caller_*).
    /// </summary>
    public static void RegisterStandaloneRoutines(TypeRegistry registry, TypeSymbol? textType,
        TypeSymbol? s64Type)
    {
        if (textType != null)
        {
            foreach (string name in new[]
                     {
                         "source_file",
                         "source_routine",
                         "source_module",
                         "source_text",
                         "caller_file",
                         "caller_routine"
                     })
            {
                RegisterStandalone(registry: registry, name: name, returnType: textType);
            }
        }

        if (s64Type != null)
        {
            foreach (string name in new[]
                     {
                         "source_line",
                         "source_column",
                         "caller_line"
                     })
            {
                RegisterStandalone(registry: registry, name: name, returnType: s64Type);
            }
        }
    }

    /// <summary>
    /// Registers the BuilderQuery platform/build info routines as standalone functions.
    /// </summary>
    public static void RegisterModuleRoutines(TypeRegistry registry, TypeSymbol? textType,
        TypeSymbol? u64Type, TypeSymbol? s64Type)
    {
        if (textType == null || u64Type == null)
        {
            return;
        }

        // Text-returning standalone routines
        foreach (string name in new[]
                 {
                     "target_os",
                     "target_arch",
                     "builder_version",
                     "build_timestamp"
                 })
        {
            RegisterStandalone(registry: registry, name: name, returnType: textType);
        }

        // build_mode returns the declared BuildMode choice only. Look it up by its MODULE-qualified name:
        // BuildMode lives in `module BuilderQuery`, so a bare `LookupType("BuildMode")` depended on the
        // cross-module short-name scan (a Core-prefix auto-import miss) — with that scan gone the bare
        // lookup returned null and `build_mode` never registered (bare call → UnknownIdentifier).
        TypeSymbol? buildModeType = registry.LookupType(name: "BuilderQuery.BuildMode");
        if (buildModeType != null)
        {
            RegisterStandalone(registry: registry, name: "build_mode", returnType: buildModeType);
        }

        // ByteSize-returning standalone routines keep their declared ByteSize surface.
        TypeSymbol? byteSizeType = registry.LookupType(name: "ByteSize");
        if (byteSizeType == null)
        {
            return;
        }

        foreach (string name in new[]
                 {
                     "page_size",
                     "cache_line",
                     "word_size"
                 })
        {
            RegisterStandalone(registry: registry, name: name, returnType: byteSizeType);
        }
    }

    /// <summary>
    /// Registers a no-parameter readonly synthesized routine if not already defined.
    /// </summary>
    private static void MaybeRegister(TypeSymbol owner, string name, TypeSymbol returnType,
        List<RoutineInfo> existingMemberRoutines, TypeRegistry registry)
    {
        if (existingMemberRoutines.Any(predicate: m => m.Name == name))
        {
            return;
        }

        registry.RegisterRoutine(routine: new RoutineInfo(name: name)
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
    /// Registers a single-parameter readonly synthesized routine if not already defined.
    /// </summary>
    private static void MaybeRegisterWithParam(TypeSymbol owner, string name, string paramName,
        TypeSymbol paramType, TypeSymbol returnType, List<RoutineInfo> existingMemberRoutines,
        TypeRegistry registry)
    {
        if (existingMemberRoutines.Any(predicate: m => m.Name == name))
        {
            return;
        }

        registry.RegisterRoutine(routine: new RoutineInfo(name: name)
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
    /// Registers a standalone (no owner type) synthesized routine if not already defined.
    /// </summary>
    private static void RegisterStandalone(TypeRegistry registry, string name,
        TypeSymbol returnType)
    {
        if (registry.LookupRoutine(fullName: name) != null)
        {
            return;
        }

        // Standalone BuilderQuery routines (target_os/build_mode/source_*/page_size/…) belong to the
        // real `module BuilderQuery`, so register them under it: name resolution then gates them by
        // normal module-import scoping — `import BuilderQuery` brings them in, and a bare call without
        // the import is a plain UnknownIdentifier, exactly like any other unimported module member.
        // (The per-TYPE reflection routines — type_id/type_name/… injected onto every type — still use
        // the dedicated import-gate, since they are not free routines you can resolve through a module.)
        registry.RegisterRoutine(routine: new RoutineInfo(name: name)
        {
            Kind = RoutineKind.Function,
            OwnerType = null,
            Module = "BuilderQuery",
            Parameters = [],
            ReturnType = returnType,
            IsFailable = false,
            DeclaredMutation = MutationCategory.Readonly,
            MutationCategory = MutationCategory.Readonly,
            Visibility = VisibilityModifier.Open,
            IsSynthesized = true
        });
    }
}
