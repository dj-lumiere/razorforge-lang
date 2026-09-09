using Compiler.Declaration;
using Compiler.Verification.Enums;
using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Types;

namespace TypeModel.Symbols;

using TypeSymbol = TypeInfo;

/// <summary>
/// Information about a routine (standalone routine, member routine, creator).
/// </summary>
public sealed class RoutineInfo
{
    /// <summary>The name of the routine (without type prefix).</summary>
    public string Name { get; }

    /// <summary>Base name for registry lookup (e.g., "Circle.draw", "Math.abs").</summary>
    public string BaseName
    {
        get
        {
            if (OwnerType != null)
            {
                return $"{OwnerType.Name}.{Name}";
            }

            return string.IsNullOrEmpty(value: Module)
                ? Name
                : $"{Module}.{Name}";
        }
    }

    /// <summary>
    /// Rich signature name for display and identity.
    /// Member: "Module.OwnerType[Generics].Name(ParamTypes) -> ReturnType".
    /// Standalone: "Module.Name[Generics](ParamTypes) -> ReturnType".
    /// </summary>
    public string FullName
    {
        get
        {
            string prefix;
            if (OwnerType != null)
            {
                string ownerName = OwnerType.GenericParameters is { Count: > 0 }
                    ? $"{OwnerType.Name}[{string.Join(separator: ", ", values: OwnerType.GenericParameters)}]"
                    : OwnerType.Name;
                prefix = string.IsNullOrEmpty(value: OwnerType.Module)
                    ? $"{ownerName}.{Name}"
                    : $"{OwnerType.Module}.{ownerName}.{Name}";
            }
            else
            {
                string routineName = GenericParameters is { Count: > 0 }
                    ? $"{Name}[{string.Join(separator: ", ", values: GenericParameters)}]"
                    : Name;
                prefix = string.IsNullOrEmpty(value: Module)
                    ? routineName
                    : $"{Module}.{routineName}";
            }

            string paramPart =
                $"({string.Join(separator: ", ", values: Parameters.Select(selector: p => p.Type.Name))})";

            return ReturnType != null
                ? $"{prefix}{paramPart} -> {ReturnType.Name}"
                : $"{prefix}{paramPart}";
        }
    }

    /// <summary>
    /// Stable key for registry lookup: "BaseName[TypeArgs]#Param1,Param2".
    /// For non-generic or unresolved routines, the type-argument segment is omitted.
    /// For zero-parameter routines, the key is just "BaseName" or "BaseName[TypeArgs]".
    /// <para>REALM: a routine whose owner belongs to a NON-ambient (bridged) realm is prefixed
    /// <c>{realm}::</c> so the two world-lines' same-signature routines are distinct EVERYWHERE this key
    /// is used in lockstep — the routine registry, <c>InstantiatedGenericBodies</c>, reachability's
    /// <c>LiveRoutineKeys</c>, and codegen's body lookup. The ambient realm is <c>RF</c> (bare), so every
    /// RazorForge routine's key is byte-identical to before; only a Suflae-realm (SF) routine gains the
    /// prefix. Keeping this on the ONE identity all consumers already share is what makes the realm split
    /// consistent without desyncing the handshake between reachability, monomorphization, and codegen.</para>
    /// </summary>
    public string RegistryKey
    {
        get
        {
            string baseName;
            if (OwnerType != null)
            {
                // A creator carries NO member name (identity is Kind==Creator). Its key is the owner
                // type identity + params — never `Owner.create`.
                baseName = IsCreator
                    ? GetTypeIdentity(type: OwnerType)
                    : $"{GetTypeIdentity(type: OwnerType)}.{Name}";
            }
            else
            {
                baseName = string.IsNullOrEmpty(value: Module) ? Name : $"{Module}.{Name}";
            }
            if (TypeArguments is { Count: > 0 })
            {
                string typeArgs = string.Join(separator: ",",
                    values: TypeArguments.Select(GetTypeIdentity));
                baseName = $"{baseName}[{typeArgs}]";
            }

            string key = Parameters.Count == 0
                ? baseName
                : $"{baseName}#{string.Join(separator: ",", values: Parameters.Select(selector: p => GetTypeIdentity(type: p.Type)))}";

            // Bridged-realm owner → realm-prefixed key (ambient realm "RF" stays bare = RF byte-identical).
            return OwnerType is { Realm: not "RF" and { } r } ? $"{r}::{key}" : key;
        }
    }

    /// <summary>
    /// Stable type identity for cache keys and overload matching.
    /// Uses fully-qualified resolved type names while preserving generic-parameter
    /// syntax for open generic definitions like <c>List[T]</c>.
    /// </summary>
    public static string GetTypeIdentity(TypeSymbol type)
    {
        if (type.GenericParameters is { Count: > 0 } && !type.Name.Contains(value: '['))
        {
            string baseName = string.IsNullOrEmpty(value: type.Module)
                ? type.Name
                : $"{type.Module}.{type.Name}";
            return
                $"{baseName}[{string.Join(separator: ", ", values: type.GenericParameters)}]";
        }

        return type.FullName;
    }

    /// <summary>The module-qualified name (e.g., "Core/S8.add", "IO/Console.show").</summary>
    public string QualifiedName
    {
        get
        {
            if (OwnerType != null)
            {
                // Member routine: Module/OwnerType.routine (e.g., "Core/S8.add")
                return $"{OwnerType.FullName}.{Name}";
            }

            // Standalone: Module.Name
            return BaseName;
        }
    }

    /// <summary>The kind of routine.</summary>
    public RoutineKind Kind { get; init; } = RoutineKind.FreeRoutine;

    /// <summary>The type that owns this routine (for member routines and extension routines).</summary>
    public TypeSymbol? OwnerType { get; init; }

    /// <summary>
    /// For a member routine declared on a SPECIALIZED generic instantiation — e.g.
    /// <c>routine List[Agent[V]].gather!()</c> — the resolved specialized receiver type
    /// (<c>List[Agent[V]]</c> with <c>V</c> a routine generic parameter). <c>me</c> is typed as this,
    /// so member access like <c>me[i]</c> yields the specialized element (<c>Agent[V]</c>) instead of
    /// the generic definition's raw element parameter. Null for ordinary members, where <c>me</c> is
    /// <see cref="OwnerType"/>. <see cref="OwnerType"/> stays the generic definition so registration
    /// and call-site lookup key on the base type.
    /// </summary>
    public TypeSymbol? MeType { get; init; }

    /// <summary>Parameters of this routine.</summary>
    public List<ParameterInfo> Parameters { get; init; } = [];

    /// <summary>
    /// For lifted lambdas (<see cref="RoutineKind.Lambda"/>): the variables the lambda body captures
    /// from its enclosing scope (name + type), in closure-field order. Drives closure conversion —
    /// the lambda compiles to a heap closure <c>{ fn_ptr, capture0, capture1, ... }</c>; the lifted
    /// function takes the closure pointer as a hidden leading parameter and loads each capture from it
    /// in its prologue. Empty/null for non-capturing lambdas (which still get the hidden parameter so
    /// the indirect-call ABI is uniform).
    /// </summary>
    public List<(string Name, TypeSymbol Type)>? ClosureCaptures { get; set; }

    /// <summary>Return type. Null means "not yet inferred" (transient during analysis). After body analysis, always None or a concrete type.</summary>
    public TypeSymbol? ReturnType { get; set; }

    /// <summary>True if the source wrote the return type with the `T` rvalue mark
    /// (entity rvalue, in-flight). Carried from <see cref="TypeExpression.IsRvalue"/>.
    /// SA enforces position validity; downstream passes use this to drive auto-bind
    /// from rvalue `T` back to lvalue `T` at the binding site.</summary>
    public bool IsInFlightReturn { get; init; }

    /// <summary>Whether this routine can fail. Set from the declared <c>!</c> suffix at registration,
    /// then RE-DERIVED by the failability-inference fixpoint (<c>InferFailableRoutines</c>) after Phase-4
    /// body analysis: a routine is failable iff it was declared <c>!</c> OR its body throws/absents OR it
    /// propagates an unhandled failable callee. The <c>internal set</c> lets that pass overwrite the
    /// declared value before codegen keys the failable-carrier ABI on it (mirrors
    /// <see cref="IsWiredMemberRoutine"/>).</summary>
    public bool IsFailable { get; internal set; }

    /// <summary>
    /// Whether this is a WIRED member routine — one the source spells with a leading <c>$</c>
    /// (<c>create</c>, <c>store</c>, <c>eq</c>, <c>emit</c>, <c>destroy</c>, …). The <c>$</c> is
    /// a STRUCTURED attribute recorded here, NOT part of <see cref="Name"/>: the canonical name is the
    /// bare identifier (<c>create</c>, <c>store</c>, …). Wired routines are always member routines.
    /// </summary>
    public bool IsWiredMemberRoutine { get; internal set; }

    /// <summary>Whether this routine contains throw statements.</summary>
    public bool HasThrow { get; set; }

    /// <summary>Whether this routine contains absent statements.</summary>
    public bool HasAbsent { get; set; }

    /// <summary>Whether this routine calls other failable routines (propagated failability).</summary>
    public bool HasFailableCalls { get; set; }

    /// <summary>
    /// Failable routines directly called by this routine. Populated during Phase 4 verification.
    /// Used by <c>ErrorHandlingVariantPass</c> to propagate <see cref="HasThrow"/> /
    /// <see cref="HasAbsent"/> / <see cref="ThrowableTypes"/> through the call graph so that
    /// routines whose failability is purely propagated (e.g. <c>return Foo!(...)</c>) get the
    /// right wrapper variants (<c>try_</c> / <c>check_</c> / <c>lookup_</c>) generated.
    /// </summary>
    public HashSet<RoutineInfo> FailableCallees { get; } = [];

    /// <summary>
    /// Concrete crashable types directly thrown by this routine (or its corresponding
    /// <c>check_</c>/<c>lookup_</c> variant). Populated after Phase 4 body analysis.
    /// Does not include types thrown by called routines (propagated throws).
    /// </summary>
    public List<TypeSymbol> ThrowableTypes { get; set; } = [];

    /// <summary>The declared mutation category for this routine (from source annotation). Defaults to
    /// <c>Writable</c> — the same "no annotation" default the resolvers apply (SignatureResolver /
    /// TypeBodyResolver): a routine mutates unless marked <c>@readonly</c>, and <c>@reshaping</c> is a
    /// STRONGER claim that must be explicit. (A <c>Reshaping</c> default was a latent bug: any construction
    /// or copy path that forgot to set the category silently made the routine look maximally-mutating,
    /// e.g. tripping the RF-S625 reshaping-during-iteration ban on a plainly <c>@readonly</c> call.)</summary>
    public MutationCategory DeclaredMutation { get; init; } =
        MutationCategory.Writable;

    /// <summary>
    /// The inferred/final mutation category for this routine.
    /// Initially set to declared value, then updated by mutation inference.
    /// </summary>
    public MutationCategory MutationCategory { get; set; } =
        MutationCategory.Writable;

    /// <summary>Generic type parameters, if any.</summary>
    public List<string>? GenericParameters { get; init; }

    /// <summary>Generic constraints on type parameters.</summary>
    public List<GenericConstraintDeclaration>? GenericConstraints { get; init; }

    /// <summary>Whether this is a generic routine definition.</summary>
    public bool IsGenericDefinition => GenericParameters is { Count: > 0 };

    /// <summary>For resolved generics, the type arguments used.</summary>
    public List<TypeSymbol>? TypeArguments { get; init; }

    /// <summary>
    /// Parameter indices whose declared type is a marker protocol (<c>Accessing[T]</c> or
    /// <c>Controlling[T]</c>). These slots participate in monomorphization: each concrete
    /// argument type at a call site produces a distinct specialization, so the protocol
    /// name never appears in mangled symbols or LLVM IR.
    /// </summary>
    public List<int> MarkerProtocolParameterIndices
    {
        get
        {
            var indices = new List<int>();
            for (int i = 0; i < Parameters.Count; i++)
            {
                if (IsMarkerProtocolType(type: Parameters[index: i].Type))
                {
                    indices.Add(item: i);
                }
            }
            return indices;
        }
    }

    private static bool IsMarkerProtocolType(TypeSymbol type)
    {
        if (type is not ProtocolTypeInfo proto || proto.TypeArguments is not { Count: 1 })
        {
            return false;
        }
        string baseName = (proto.GenericDefinition ?? proto).BareName;
        return RuntimeContract.IsMarkerProtocol(baseName: baseName);
    }

    /// <summary>Visibility modifier.</summary>
    public VisibilityModifier Visibility { get; init; } = VisibilityModifier.Open;

    /// <summary>Source location where this routine is defined.</summary>
    public SourceLocation? Location { get; init; }

    /// <summary>The <c>###</c> documentation-comment text written above the routine's declaration (lines
    /// joined with newlines), or null. Carried from <c>Declaration.Documentation</c> at registration so
    /// the language server can show it on hover / completion — for stdlib routines too, since the stdlib
    /// is parsed the same way.</summary>
    public string? Documentation { get; init; }

    /// <summary>
    /// Structural hash of a CONSTRUCTOR body, set at registration for the divergent-duplicate guard.
    /// Two constructors can share a signature across files (e.g. <c>U16(from: U8)</c> in both U8.rf and
    /// U16.rf) — benign when the bodies are identical (same hash), but a DIVERGENT one (same signature,
    /// different body) means one silently shadows the other under last-wins registration, the hazard
    /// class that made <c>F64(from: F128)</c> resolve to a recursive-forwarder stub. Null for non-creators
    /// / extern bodies. See <see cref="Compiler.Declaration.TypeRegistry.RegisterRoutine"/>.
    /// </summary>
    public int? BodyHash { get; set; }

    /// <summary>The module this routine belongs to.</summary>
    public string? Module { get; init; }

    /// <summary>Module path segments (e.g., ["Core", "Memory", "Wrapper"]).</summary>
    public List<string>? ModulePath { get; init; }

    /// <summary>Annotations on this routine (e.g., @readonly, @inline).</summary>
    public List<string> Annotations { get; init; } = [];

    /// <summary>Whether this routine is marked @readonly (can be called through Viewing/Consulting).</summary>
    public bool IsReadOnly =>
        Annotations.Contains(value: "readonly") || MutationCategory == MutationCategory.Readonly;

    /// <summary>Whether this routine RESHAPES its receiver (a container mutator that can invalidate an
    /// in-progress iteration — drives the RF-S625 reshaping-during-iteration ban). The <c>@reshaping</c>
    /// annotation is the authoritative marker (container authors put it on mutators); the category is a
    /// fallback, and is NOT trusted alone because some registration paths leave it at the default.</summary>
    public bool IsReshaping =>
        Annotations.Contains(value: "reshaping") || MutationCategory == MutationCategory.Reshaping;

    /// <summary>
    /// For external("llvm") routines, the LLVM IR template from @llvm_ir annotation.
    /// Extracted from annotations at access time; null if no @llvm_ir annotation.
    /// </summary>
    public string? LlvmIrTemplate
    {
        get
        {
            foreach (string annotation in Annotations)
            {
                if (!annotation.StartsWith(value: "llvm_ir("))
                {
                    continue;
                }

                // Extract template: llvm_ir("template") or llvm_ir(template).
                // The tokenizer already decodes string escapes and strips the outer
                // delimiters, so the annotation text is `llvm_ir(<decoded template>)`.
                // A template may itself CONTAIN quotes (e.g. inline-asm constraint
                // strings: `asm sideeffect "", "=r,0"(...)`), so we must NOT scan for
                // the first/last '"' to find the bounds — that would slice the span
                // between inner quotes. Strip the `llvm_ir(` prefix and trailing `)`,
                // then strip exactly one wrapping quote pair only if BOTH ends are quotes.
                ReadOnlySpan<char> content = annotation.AsSpan()["llvm_ir(".Length..];
                if (content.Length > 0 && content[^1] == ')')
                {
                    content = content[..^1];
                }

                if (content.Length >= 2 && content[0] == '"' && content[^1] == '"')
                {
                    content = content[1..^1];
                }

                return content.ToString();
            }

            return null;
        }
    }

    /// <summary>For external routines, the calling convention.</summary>
    public string? CallingConvention { get; init; }

    /// <summary>
    /// The DECLARED library this foreign routine is linked from — the name in its <c>@link(Lib)</c>
    /// attribute, matching a <c>library Lib …</c> declaration. Null for a static/ambient <c>C::</c> extern
    /// (resolved from libc / a toml archive) and for <c>LLVM::</c> intrinsics. Whether that library links
    /// STATICALLY or DYNAMICALLY, and under which calling convention, is a property of the LIBRARY (the
    /// <c>library</c> declaration), NOT of this routine — so switching a library static↔dynamic never
    /// touches call sites. The realm stays <c>C::</c> (= no-mangle C-ABI) regardless, so ABI/mangling are
    /// unaffected. See <see cref="LinkSymbol"/> for a symbol-name override.
    /// </summary>
    public string? LinkLibrary { get; init; }

    /// <summary>
    /// The actual exported symbol name to link against, when it differs from <see cref="Name"/> — from
    /// <c>@link(Lib, symbol: "…")</c>. Handles the "same RF name, different link name" cases (versioned
    /// symbols, <c>stat</c>→<c>stat64</c>, decorated names). Null = link by the bare routine name.
    /// </summary>
    public string? LinkSymbol { get; init; }

    /// <summary>
    /// The realm this routine's implementation lives in. DERIVED from <see cref="CallingConvention"/> for
    /// the foreign realms (so it rides along automatically wherever CallingConvention is copied —
    /// monomorphization, reachability, etc.), falling back to the native realm otherwise. A `C::`/`LLVM::`
    /// declaration sets CallingConvention "C"/"llvm"; a native routine leaves it null and is RF (or SF via
    /// <see cref="NativeRealm"/>). Replaces the old <c>RoutineKind.External</c> flag.
    /// </summary>
    public TypeModel.Enums.RoutineRealm Realm => CallingConvention switch
    {
        "C" => TypeModel.Enums.RoutineRealm.C,
        "llvm" => TypeModel.Enums.RoutineRealm.LLVM,
        _ => NativeRealm
    };

    /// <summary>Native realm for a non-foreign routine (RF for a <c>.rf</c> body, SF for a <c>.sf</c> body).
    /// Defaults to RF; ignored when the routine is C/LLVM foreign.</summary>
    public TypeModel.Enums.RoutineRealm NativeRealm { get; init; } = TypeModel.Enums.RoutineRealm.RF;

    /// <summary>True if this routine is a FOREIGN declaration (C extern or LLVM intrinsic) — no native
    /// body; must be called with its realm qualifier. Supersedes <c>Kind == RoutineKind.External</c>.</summary>
    public bool IsForeign => Realm is TypeModel.Enums.RoutineRealm.C or TypeModel.Enums.RoutineRealm.LLVM;

    /// <summary>For external routines, whether it's variadic.</summary>
    public bool IsVariadic { get; init; }

    /// <summary>Whether this routine is marked dangerous (requires danger block to call).</summary>
    public bool IsDangerous { get; init; }

    /// <summary>Whether this routine is a common (type-level static) routine — now a <see cref="RoutineKind"/>
    /// (the former orthogonal <c>StorageClass.Common</c> axis was folded into the routine kind).</summary>
    public bool IsCommon => Kind == RoutineKind.CommonRoutine;

    /// <summary>Whether this routine is a lambda / closure expression.</summary>
    public bool IsLambda => Kind == RoutineKind.Lambda;

    /// <summary>Whether this routine is a constructor. Structured identity — a constructor is identified
    /// ONLY by its <see cref="RoutineKind.Creator"/> kind, NEVER by a name string. Creators carry no
    /// member name (see <see cref="CreatorName"/>).</summary>
    public bool IsCreator => Kind == RoutineKind.Creator;

    /// <summary>The (empty) name every creator carries: a constructor has NO member name — its identity is
    /// its <see cref="RoutineKind.Creator"/> kind. Registration assigns this; lookup/mangling special-case
    /// creators by <see cref="IsCreator"/>. Kept as a named constant so the intent is explicit and the old
    /// reserved <c>"create"</c> literal appears nowhere.</summary>
    public const string CreatorName = "";

    /// <summary>Whether this routine was auto-generated (e.g., derived comparison operators).</summary>
    public bool IsSynthesized { get; init; }

    /// <summary>
    /// For wrapper-forwarder synthesized routines: the inner-type member routine that this forwarder
    /// delegates to. Used by monomorphization to re-resolve signatures against the concrete
    /// inner type when the inner member routine's return depends on the inner's generic parameter.
    /// </summary>
    public RoutineInfo? WrapperForwarderInnerMemberRoutine { get; init; }

    /// <summary>
    /// For wrapper-forwarder synthesized routines: the inner type's generic definition
    /// (e.g. List[T] when wrapping List[T]). Used to look up the concrete inner
    /// member routine after monomorphization.
    /// </summary>
    public TypeSymbol? WrapperForwarderInnerGenericDef { get; init; }

    /// <summary>The suspended or threaded status of this routine (None, Suspended, Threaded).</summary>
    public AsyncStatus AsyncStatus { get; init; } = AsyncStatus.None;

    /// <summary>
    /// Which compiler-generated failable wrapper this routine is, if any (None for ordinary
    /// routines). Orthogonal to <see cref="AsyncStatus"/> — previously the lookup_/check_/try_
    /// variants were mixed into AsyncStatus and are now tracked separately here.
    /// </summary>
    public FailableVariant FailableVariant { get; init; } = FailableVariant.None;

    /// <summary>Whether this routine is a suspended routine.</summary>
    public bool IsSuspended => AsyncStatus is AsyncStatus.Suspended;

    /// <summary>Whether this routine is a threaded (OS-thread) routine.</summary>
    public bool IsThreaded => AsyncStatus is AsyncStatus.Threaded;

    /// <summary>Whether this routine is any kind of suspended or threaded routine.</summary>
    public bool IsAsync => IsSuspended || IsThreaded;

    /// <summary>
    /// For generic definitions, the original generic routine this was resolved from.
    /// </summary>
    public RoutineInfo? GenericDefinition { get; init; }

    /// <summary>
    /// For generated error-handling variants (try_, check_, lookup_), the original routine name
    /// they were generated from (e.g., "emit" for "try_emit", "parse" for "try_parse").
    /// </summary>
    public string? OriginalName { get; init; }

    /// <summary>
    /// Initializes a new instance of the <see cref="RoutineInfo"/> class.
    /// </summary>
    /// <param name="name">The name of the routine.</param>
    public RoutineInfo(string name)
    {
        Name = name;
    }

    /// <summary>
    /// Creates a resolved version of this generic routine with the given type arguments.
    /// </summary>
    /// <param name="typeArguments">The type arguments to substitute for generic parameters.</param>
    /// <returns>A new <see cref="RoutineInfo"/> with types substituted.</returns>
    /// <exception cref="InvalidOperationException">Thrown if this is not a generic definition.</exception>
    /// <exception cref="ArgumentException">Thrown if the number of type arguments doesn't match.</exception>
    public RoutineInfo CreateInstance(List<TypeSymbol> typeArguments)
    {
        if (!IsGenericDefinition)
        {
            throw new InvalidOperationException(
                message: $"Routine '{Name}' is not a generic definition.");
        }

        if (typeArguments.Count != GenericParameters!.Count)
        {
            throw new ArgumentException(
                message:
                $"Expected {GenericParameters.Count} type arguments, got {typeArguments.Count}.");
        }

        // Create type parameter substitution map
        var substitution = new Dictionary<string, TypeSymbol>();
        for (int i = 0; i < GenericParameters.Count; i++)
        {
            substitution[key: GenericParameters[index: i]] = typeArguments[index: i];
        }

        // Substitute types in parameters
        var substitutedParams = Parameters
                               .Select(selector: p =>
                                    SubstituteParameterType(param: p, substitution: substitution))
                               .ToList();

        // Substitute return type
        TypeSymbol? substitutedReturnType = ReturnType != null
            ? SubstituteType(type: ReturnType, substitution: substitution)
            : null;

        return new RoutineInfo(name: Name)
        {
            Kind = Kind,
            OwnerType = OwnerType,
            Parameters = substitutedParams,
            ReturnType = substitutedReturnType,
            IsFailable = IsFailable,
            IsWiredMemberRoutine = IsWiredMemberRoutine,
            DeclaredMutation = DeclaredMutation,
            MutationCategory = MutationCategory,
            TypeArguments = typeArguments,
            // Preserve universal self-type provenance through this resolution layer. For a
            // self-type extension member routine (`routine T.share[P]()`), `LookupMemberRoutine` returns an
            // owner-bound intermediate (OwnerType already = the concrete receiver) whose
            // GenericDefinition points at the original universal member routine (OwnerType = the bare
            // `T` generic parameter). If we naively set GenericDefinition = this, that universal
            // provenance is buried one level down, and reachability / GMP (which gate the
            // self-type owner→receiver binding on `GenericDefinition.OwnerType is
            // GenericParameterTypeInfo`) can no longer recover `T → receiver` — the concrete
            // receiver carries no TypeArguments to recover it from, unlike `List[S32].MemberRoutine[U]`.
            // The result is an emitted call to `Receiver.share[P]` with no matching definition
            // (LINKERR). Keep the universal member routine as the definition so that binding survives.
            GenericDefinition = GenericDefinition?.OwnerType is GenericParameterTypeInfo
                ? GenericDefinition
                : this,
            Visibility = Visibility,
            Location = Location,
            Documentation = Documentation,
            Module = Module,
            ModulePath = ModulePath,
            Annotations = Annotations,
            CallingConvention = CallingConvention,
            LinkLibrary = LinkLibrary,
            LinkSymbol = LinkSymbol,
            IsVariadic = IsVariadic,
            IsDangerous = IsDangerous,
            IsSynthesized = IsSynthesized,
            WrapperForwarderInnerMemberRoutine = WrapperForwarderInnerMemberRoutine,
            WrapperForwarderInnerGenericDef = WrapperForwarderInnerGenericDef,
            AsyncStatus = AsyncStatus,
            FailableVariant = FailableVariant
        };
    }

    /// <summary>
    /// Substitutes the type in a parameter for generic resolution.
    /// </summary>
    /// <param name="param">The parameter to substitute.</param>
    /// <param name="substitution">The type parameter substitution map.</param>
    /// <returns>A new <see cref="ParameterInfo"/> with the substituted type.</returns>
    internal static ParameterInfo SubstituteParameterType(ParameterInfo param,
        Dictionary<string, TypeSymbol> substitution)
    {
        TypeSymbol substitutedType = SubstituteType(type: param.Type, substitution: substitution);
        return param.WithSubstitutedType(newType: substitutedType);
    }

    /// <summary>
    /// Recursively substitutes type parameters in a type.
    /// </summary>
    /// <param name="type">The type to substitute.</param>
    /// <param name="substitution">The type parameter substitution map.</param>
    /// <returns>The substituted type, or the original if no substitution applies.</returns>
    internal static TypeSymbol SubstituteType(TypeSymbol type,
        Dictionary<string, TypeSymbol> substitution)
    {
        // Associated-type projection (`S/Iter`): substitute the base, then resolve via the base's
        // binding (instance, or generic-definition fallback). Mirrors RecordTypeInfo.SubstituteType
        // so reachability/instantiation paths that route through here also resolve projections.
        if (type is AssociatedProjectionTypeInfo projection)
        {
            return SubstituteAssociatedProjection(projection: projection, substitution: substitution);
        }

        // Comptime const-generic (`${max(T.data_size().byte_size(), 8)}`): once the referenced type
        // params are bound, fold to a plain ConstGenericValueTypeInfo; otherwise keep it symbolic.
        if (type is ComptimeConstGenericTypeInfo comptime)
        {
            return SubstituteComptimeConstGeneric(comptime: comptime, substitution: substitution);
        }

        if (substitution.TryGetValue(key: type.Name, value: out TypeSymbol? substituted))
        {
            return substituted;
        }

        // Substitute inside routine types (e.g., Routine[(T, T), Bool] -> Routine[(S64, S64), Bool])
        if (type is RoutineTypeInfo routineType)
        {
            return SubstituteRoutineType(routineType: routineType, substitution: substitution);
        }

        if (type is TupleTypeInfo tupleType)
        {
            return SubstituteTupleType(tupleType: tupleType, substitution: substitution);
        }

        if (type is { IsGenericResolution: true, TypeArguments: not null })
        {
            return SubstituteGenericResolution(type: type, substitution: substitution);
        }

        return type;
    }

    // Associated-type projection (`S/Iter`): substitute the base, resolve via its binding, else re-base.
    private static TypeSymbol SubstituteAssociatedProjection(AssociatedProjectionTypeInfo projection,
        Dictionary<string, TypeSymbol> substitution)
    {
        TypeSymbol newBase = SubstituteType(type: projection.Base, substitution: substitution);
        TypeInfo? bound = RecordTypeInfo.ProjectAssociatedBinding(baseType: newBase,
            slot: projection.SlotName);
        if (bound != null)
        {
            return SubstituteType(type: bound, substitution: substitution);
        }
        return ReferenceEquals(objA: newBase, objB: projection.Base)
            ? projection
            : new AssociatedProjectionTypeInfo(baseType: newBase, slotName: projection.SlotName);
    }

    // Comptime const-generic: fold once referenced params are bound, else keep symbolic.
    private static TypeSymbol SubstituteComptimeConstGeneric(ComptimeConstGenericTypeInfo comptime,
        Dictionary<string, TypeSymbol> substitution)
    {
        return comptime.TryFold(
                resolveTypeParam: name => substitution.TryGetValue(key: name, value: out TypeSymbol? s)
                    ? s
                    : null,
                pointerSize: 8, out long folded)
            ? new ConstGenericValueTypeInfo(literalText: folded.ToString(),
                value: folded,
                explicitTypeName: "U64")
            : comptime;
    }

    // Substitute inside a routine type's parameter and return types.
    private static RoutineTypeInfo SubstituteRoutineType(RoutineTypeInfo routineType,
        Dictionary<string, TypeSymbol> substitution)
    {
        var substitutedParams = routineType.ParameterTypes
            .Select(selector: p => SubstituteType(type: p, substitution: substitution))
            .ToList();
        TypeSymbol? substitutedReturn = routineType.ReturnType != null
            ? SubstituteType(type: routineType.ReturnType, substitution: substitution)
            : null;
        return new RoutineTypeInfo(parameterTypes: substitutedParams,
            returnType: substitutedReturn) { IsFailable = routineType.IsFailable };
    }

    // Substitute inside a tuple type's element types.
    private static TupleTypeInfo SubstituteTupleType(TupleTypeInfo tupleType,
        Dictionary<string, TypeSymbol> substitution)
    {
        var substitutedElements = tupleType.ElementTypes
            .Select(selector => SubstituteType(type: selector, substitution: substitution))
            .ToList();
        bool anyChanged = substitutedElements.Where((element, index) =>
                !ReferenceEquals(objA: element, objB: tupleType.ElementTypes[index: index]))
            .Any();
        return anyChanged
            ? new TupleTypeInfo(elementTypes: substitutedElements)
            : tupleType;
    }

    // Substitute a generic resolution's args and re-resolve through the ambient registry per kind.
    private static TypeSymbol SubstituteGenericResolution(TypeSymbol type,
        Dictionary<string, TypeSymbol> substitution)
    {
        var newArgs = type.TypeArguments!
                          .Select(selector: arg =>
                               SubstituteType(type: arg, substitution: substitution))
                          .ToList();

        // Route through the ambient TypeRegistry so entity-type specializations
        // (e.g. Maybe[Text] -> { Hijacked[T] } layout) are picked up instead of
        // blindly using the primary generic definition's layout.
        TypeRegistry? registry = TypeRegistry.Ambient;

        // Use GenericDefinition to create the new resolution (not the resolution itself)
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

        // WrapperTypeInfo (Retained[T], Guarded[T], etc.) — if the registry has a RecordTypeInfo
        // for the same base name, prefer that so the concrete type stays RecordTypeInfo everywhere.
        // This avoids the WrapperTypeInfo -> "ptr" codegen mapping mismatch when the actual LLVM
        // function definition uses the struct layout from the RecordTypeInfo.
        if (type is WrapperTypeInfo && registry != null)
        {
            TypeInfo? recordDef = registry.LookupType(name: type.Name);
            if (recordDef is RecordTypeInfo { IsGenericDefinition: true })
            {
                return registry.GetOrCreateResolution(genericDef: recordDef, typeArguments: newArgs);
            }
        }

        return type.CreateInstance(typeArguments: newArgs);
    }
}
