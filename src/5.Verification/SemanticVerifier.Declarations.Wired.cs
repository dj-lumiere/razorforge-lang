using Compiler.Diagnostics;
using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;
using Compiler.Instantiation;

namespace Compiler.Verification;

using TypeSymbol = TypeInfo;

/// <summary>
/// Phase 5: Wired routine synthesis and derived operator generation.
/// </summary>
public sealed partial class SemanticVerifier
{
    /// <summary>
    /// Known wired memberRoutines that are valid operator/special memberRoutines. Derived from the single source
    /// of truth <see cref="Compiler.Declaration.WiredRoutineCatalog"/> (entries flagged
    /// <see cref="Compiler.Declaration.WiredViews.KnownWired"/>).
    /// </summary>
    private static readonly HashSet<string> KnownWiredMemberRoutines =
        Declaration.WiredRoutineCatalog.BuildKnownWiredMemberRoutines();

    /// <summary>
    /// Checks whether a routine declared with the wired '$' sigil names something that is NOT a known
    /// built-in wired memberRoutine. The canonical name is bare ('$' is a structured attribute, not part of
    /// the name), so wired-ness is passed via <paramref name="isWired"/> rather than sniffed from the
    /// string. A non-wired routine can be named anything and is never flagged.
    /// </summary>
    private static bool IsUnknownWiredMemberRoutine(string bareName, bool isWired)
    {
        if (!isWired || bareName.Length == 0)
        {
            return false;
        }

        return !KnownWiredMemberRoutines.Contains(value: bareName);
    }

    /// <summary>
    /// Maps operator wired memberRoutines to their required protocols. Types must follow the protocol to
    /// define the operator memberRoutine. Derived from the single source of truth
    /// <see cref="Compiler.Declaration.WiredRoutineCatalog"/> (entries flagged
    /// <see cref="Compiler.Declaration.WiredViews.ProtocolDecl"/>).
    /// </summary>
    private static readonly Dictionary<string, List<string>> WiredToProtocols =
        Declaration.WiredRoutineCatalog.BuildWiredToProtocols();

    /// <summary>
    /// Gets the required protocol for a wired memberRoutine, or null if no protocol is required.
    /// </summary>
    internal static List<string>? GetRequiredProtocols(string wiredName)
    {
        return WiredToProtocols.GetValueOrDefault(key: wiredName);
    }

    /// <summary>
    /// Re-derives <see cref="RoutineInfo.IsWiredMemberRoutine"/> for every registered member routine.
    ///
    /// <para>The wired sigil (<c>$</c>) was removed from the surface syntax, so the parser no longer
    /// sets this flag (it is uniformly false at registration). This pass restores meaningful wired-ness
    /// by INFERRING it from the wired-routine catalog plus explicit protocol conformance.</para>
    ///
    /// <para>A member routine <c>R</c> is wired iff its bare name is a known wired memberRoutine AND either the
    /// name is not protocol-gated (creator/context/lifecycle names like <c>create</c>/<c>destroy</c> —
    /// wired by catalog name alone) or the owner EXPLICITLY <c>obeys</c> one of the name's required
    /// protocols. This reproduces the pre-removal <c>$</c> set: a numeric <c>add</c> (obeys Addable) is
    /// wired, while <c>Set.add</c> (Set does not obey Addable) stays non-wired.</para>
    ///
    /// <para>Runs AFTER conformance is applied (<c>ApplyImplicitMarkerConformance</c> + user <c>obeys</c>)
    /// and AFTER all member routines are registered, so the explicit-conformance query is authoritative.
    /// It OVERWRITES the all-false value the parser left behind.</para>
    /// </summary>
    private void InferWiredMemberRoutines()
    {
        foreach (RoutineInfo r in _registry.EnumerateMemberRoutines())
        {
            r.IsWiredMemberRoutine = InferWired(r: r);
        }
    }

    /// <summary>Computes wired-ness for a single member routine (see <see cref="InferWiredMemberRoutines"/>).</summary>
    private bool InferWired(RoutineInfo r)
    {
        if (r.OwnerType == null || !KnownWiredMemberRoutines.Contains(value: r.Name))
        {
            return false;
        }

        List<string>? protos = GetRequiredProtocols(wiredName: r.Name);
        if (protos == null || protos.Count == 0)
        {
            // create/destroy/enter/exit/from_literal/unwrap/… — wired by catalog name alone.
            return true;
        }

        // Re-lookup the owner to get the version whose ImplementedProtocols are populated by conformance.
        TypeSymbol? owner = _registry.LookupType(name: r.OwnerType.FullName) ?? r.OwnerType;
        return protos.Any(predicate: p =>
            ExplicitlyImplementsProtocol(type: owner, protocolName: p));
    }

    /// <summary>
    /// Re-derives <see cref="RoutineInfo.IsFailable"/> for every registered routine from INFERENCE,
    /// after Phase-4 body analysis has populated <see cref="RoutineInfo.HasThrow"/> /
    /// <see cref="RoutineInfo.HasAbsent"/> / <see cref="RoutineInfo.FailableCallees"/>.
    ///
    /// <para>A routine is failable iff it was DECLARED <c>!</c> (kept — the annotation is now OPTIONAL
    /// but honest) OR its body directly <c>throw</c>s / <c>absent</c>s. The declaration <c>!</c> is
    /// never REMOVED by inference; inference only ADDS failability to a routine that throws/absents
    /// without a declared <c>!</c> (the newly-allowed un-declared-failable case).</para>
    ///
    /// <para>Failability is NOT propagated through the call graph here: a non-failable routine calling
    /// a failable one is the language's established CRASH-ONLY path (the call fails ⇒ the program
    /// crashes), and that caller keeps its non-failable ABI. Making every such caller failable would
    /// rewrite the failable-carrier ABI of a large fraction of the stdlib for no behavioural gain.
    /// (Purely-PROPAGATED failability of a routine that IS declared <c>!</c> — e.g. a <c>!</c> wrapper
    /// whose body only returns an inner <c>!</c> call — is preserved because its declared <c>!</c>
    /// seeds it here; <c>ErrorHandlingVariantPass</c> still fans throw/absent through
    /// <see cref="RoutineInfo.FailableCallees"/> for variant generation.)</para>
    ///
    /// <para>Runs after ALL bodies are analyzed and BEFORE codegen, which keys the failable-carrier ABI
    /// on <see cref="RoutineInfo.IsFailable"/>. Mirrors <see cref="InferWiredMemberRoutines"/>: a
    /// post-analysis pass that overwrites a declared flag with the derived value. On a codebase where
    /// every throwing routine is already declared <c>!</c> this is a no-op (inference AGREES with the
    /// declarations), so it stays ABI-consistent — verified by the stdlib harness.</para>
    /// </summary>
    private void InferFailableRoutines()
    {
        foreach (RoutineInfo r in _registry.GetAllRoutines())
        {
            if (r.HasThrow || r.HasAbsent)
            {
                r.IsFailable = true;
            }
        }
    }

    private void CollectExternalDeclaration(ExternalDeclaration external)
    {
        // #123: Suflae cannot use C interop directly
        if (_registry.Language == Language.Suflae)
        {
            ReportError(code: SemanticDiagnosticCode.SuflaeNoCInterop,
                message:
                $"Suflae does not support C interop. External declaration '{external.Name}' is not allowed. " +
                "Use RazorForge for native interop.",
                location: external.Location);
        }

        // Resolve the parameter + return types — WITHOUT this the routine registers with zero
        // parameters, so every `C::foo(arg)` call fails RF-S501 "expects at most 0 arguments" (the
        // stdlib load path already does this in RegisterExternalDeclaration; the user-file path had
        // silently dropped them). A generic-param context stub lets ResolveType recognize the
        // external's own `[T]` params; ordinary FFI param types (S32/U64/CStr/Hijacked[T]/a choice)
        // are already registered by the time this Phase-1 collection runs.
        RoutineInfo? prevRoutine = _currentRoutine;
        _currentRoutine = new RoutineInfo(name: external.Name)
        {
            GenericParameters = external.GenericParameters
        };

        var parameters = new List<ParameterInfo>();
        foreach (Parameter param in external.Parameters)
        {
            TypeSymbol paramType = ResolveType(typeExpr: param.Type);
            parameters.Add(item: new ParameterInfo(name: param.Name, type: paramType)
            {
                DefaultValue = param.DefaultValue, IsVariadicParam = param.IsVariadic
            });
        }

        TypeSymbol? returnType = external.ReturnType != null
            ? ResolveType(typeExpr: external.ReturnType)
            : null;

        _currentRoutine = prevRoutine;

        // `@link(lib: "X", symbol: "y")` associates this C:: extern with a foreign library and (optionally)
        // overrides the linked symbol name. Linkage kind (static/dynamic) + calling convention live in the
        // toml [libraries.X] declaration, NOT here — so switching a library static↔dynamic never touches
        // source. The FIRST @link attribute wins.
        (string? linkLibrary, string? linkSymbol) =
            ExtractLinkBinding(annotations: external.Annotations);

        var routineInfo = new RoutineInfo(name: external.Name)
        {
            // Foreign-ness is now carried by RoutineInfo.Realm (derived from CallingConvention).
            IsFailable = external.IsFailable,
            CallingConvention = external.CallingConvention,
            LinkLibrary = linkLibrary,
            LinkSymbol = linkSymbol,
            IsVariadic = external.IsVariadic,
            Parameters = parameters,
            ReturnType = returnType,
            Visibility = VisibilityModifier.Open, // External declarations are always open
            Location = external.Location,
            Module = GetCurrentModuleName(),
            Annotations = external.Annotations ?? [],
            IsDangerous = external.IsDangerous,
            GenericParameters = external.GenericParameters,
            GenericConstraints = external.GenericConstraints
        };

        _registry.RegisterRoutine(routine: routineInfo);
    }

    /// <summary>
    /// Scans a foreign declaration's annotations for the first <c>@link(...)</c> attribute and returns its
    /// (library, symbol-override) binding. Both null when there is no <c>@link</c> — an ambient/static
    /// <c>C::</c> extern resolved from libc / a toml archive.
    /// </summary>
    private static (string? Library, string? Symbol) ExtractLinkBinding(List<string>? annotations)
    {
        if (annotations == null)
        {
            return (null, null);
        }

        foreach (string ann in annotations)
        {
            (string? lib, string? symbol) = LinkAnnotation.Parse(annotation: ann);
            if (lib != null || symbol != null)
            {
                return (lib, symbol);
            }
        }

        return (null, null);
    }

    private void TryRegisterType(TypeSymbol type, SourceLocation location)
    {
        try
        {
            _registry.RegisterType(type: type);
        }
        catch (InvalidOperationException)
        {
            ReportError(code: SemanticDiagnosticCode.DuplicateTypeDefinition,
                message: $"Type '{type.Name}' is already defined.",
                location: location);
        }
    }

    #region Phase 5: Derived Operator Generation

    /// <summary>
    /// Generates derived comparison operators from eq and cmp routines.
    /// </summary>
    private void GenerateDerivedOperators()
    {
        new DerivedOperatorPass(registry: _registry,
            synthesizedBodies: _synthesizedBodies,
            errors: _errors).Run();
    }

    #endregion
}
