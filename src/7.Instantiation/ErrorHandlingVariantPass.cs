using Compiler.Desugaring;
using Compiler.Declaration;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Compiler.Instantiation;

/// <summary>
/// Generates try_/check_/lookup_ routine variants for all failable routines.
/// Runs once globally after Phase 4 body analysis.
///
/// Generation rules (based on throw/absent found in body):
/// - Only absent:       try_
/// - Only throw:        try_ + check_
/// - Both:              try_ + lookup_
/// </summary>
internal sealed class ErrorHandlingVariantPass(DesugaringContext ctx)
{
    private const string PrefixTry = "try";
    private const string PrefixCheck = "check";
    private const string PrefixLookup = "lookup";

    /// <summary>
    /// Per-file stub: variant generation is global only (see <see cref="RunGlobal"/>).
    /// This overload intentionally does nothing.
    /// </summary>
    public static void Run(Program program)
    {
        // Variant generation is a single global pass (RunGlobal); there is no per-file work to do.
    }

    /// <summary>
    /// Runs variant generation globally.
    /// Must be called once after all routine bodies have been analyzed (Phase 4).
    /// </summary>
    public void RunGlobal()
    {
        var generator = new ErrorHandlingGenerator(registry: ctx.Registry);

        // Snapshot before iteration -> registering variants adds new routines to the registry
        var routines = ctx.Registry
                          .GetAllRoutines()
                          .ToList();

        PopulateDirectFailability(routines: routines);
        MarkPessimisticStdlibFailability(routines: routines);
        PropagateFailabilityFixpoint(routines: routines);

        List<(RoutineInfo routine, Statement body, List<GeneratedVariant> variants)> pending =
            RegisterVariants(routines: routines, generator: generator);
        TransformPendingBodies(pending: pending);
    }

    /// <summary>
    /// Phase A: populate per-routine HasThrow/HasAbsent/ThrowableTypes from direct body
    /// scan. (Verifier sets HasThrow/HasAbsent for direct cases; we also need ThrowableTypes
    /// populated before propagation can fan them out through the call graph.)
    /// </summary>
    private void PopulateDirectFailability(List<RoutineInfo> routines)
    {
        foreach (RoutineInfo routine in routines.Where(predicate: r =>
                     r.IsFailable && ctx.RoutineBodies.ContainsKey(key: r.RegistryKey)))
        {
            Statement body = ctx.RoutineBodies[key: routine.RegistryKey];
            ErrorHandlingAnalysis analysis = ErrorHandlingGenerator.AnalyzeBody(body: body);
            if (analysis.HasThrow)
            {
                routine.HasThrow = true;
            }

            if (analysis.HasAbsent)
            {
                routine.HasAbsent = true;
            }

            foreach (TypeInfo t in analysis.ThrownTypes.Where(predicate: t =>
                         !routine.ThrowableTypes.Contains(item: t)))
            {
                routine.ThrowableTypes.Add(item: t);
            }
        }
    }

    /// <summary>
    /// Phase A2: stdlib bodies are stored by CollectStdlibBodiesForVariantGeneration
    /// without running SA, so propagated-failability routines (e.g. stdlib
    /// `common routine S64.from_digit_bytes!` returning `S64.from_digit_bytes_at!`) have
    /// empty FailableCallees and no direct throw/absent. Detect them and mark pessimistic
    /// so variant generation produces try_ + lookup_ — matching what the pre-register
    /// pass registered as stubs.
    /// </summary>
    private void MarkPessimisticStdlibFailability(List<RoutineInfo> routines)
    {
        foreach (RoutineInfo routine in routines.Where(predicate: r =>
                     r.IsFailable && !r.HasThrow && !r.HasAbsent && r.FailableCallees.Count == 0 &&
                     ctx.RoutineBodies.ContainsKey(key: r.RegistryKey)))
        {
            routine.HasThrow = true;
            routine.HasAbsent = true;
        }
    }

    /// <summary>
    /// Phase B: fixpoint propagation through FailableCallees. A routine whose failability
    /// is purely propagated (e.g. routine S64_from_text! returning S64.create!(from_text: t))
    /// has HasThrow=HasAbsent=false but FailableCallees containing S64.create!.
    /// We OR the callees' state into the caller until no further change.
    /// </summary>
    private static void PropagateFailabilityFixpoint(List<RoutineInfo> routines)
    {
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (RoutineInfo routine in routines.Where(predicate: r => r.IsFailable))
            {
                foreach (RoutineInfo callee in routine.FailableCallees)
                {
                    changed |= PropagateCalleeFailability(routine: routine, callee: callee);
                }
            }
        }
    }

    /// <summary>
    /// Merges one callee's HasThrow, HasAbsent, and ThrowableTypes flags into the caller routine.
    /// Returns true when any flag was newly set (signals that the fixpoint should continue).
    /// </summary>
    private static bool PropagateCalleeFailability(RoutineInfo routine, RoutineInfo callee)
    {
        bool changed = false;

        if (callee.HasThrow && !routine.HasThrow)
        {
            routine.HasThrow = true;
            changed = true;
        }

        if (callee.HasAbsent && !routine.HasAbsent)
        {
            routine.HasAbsent = true;
            changed = true;
        }

        var newTypes = callee.ThrowableTypes
                             .Where(predicate: t => !routine.ThrowableTypes.Contains(item: t))
                             .ToList();
        if (newTypes.Count > 0)
        {
            routine.ThrowableTypes.AddRange(collection: newTypes);
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// Phase C: register all variants first (no body transformation yet) — so the body
    /// rewriter in Phase D can find variants of callees regardless of iteration order.
    /// Returns the per-routine work items to transform in Phase D.
    /// </summary>
    private List<(RoutineInfo routine, Statement body, List<GeneratedVariant> variants)>
        RegisterVariants(List<RoutineInfo> routines, ErrorHandlingGenerator generator)
    {
        var pending =
            new List<(RoutineInfo routine, Statement body, List<GeneratedVariant> variants)>();

        // DEMAND-DRIVEN: only the iterator `emit` variants are generated eagerly here (their generic-def
        // bodies must exist before Phase-8 monomorphization of composed emitters). EVERY OTHER failable's
        // try_/check_/lookup_ variant — body and registration — is produced ON DEMAND the first time a call
        // site reaches it (SemanticVerifier's TrySynthesizeVariantOnDemand → GenerateVariantBody, drained
        // before AnalyzeVariantBodies). This is what stops ~3600 stdlib variant bodies from being built +
        // analyzed every run when a program uses only a handful.
        foreach (RoutineInfo routine in routines.Where(predicate: r =>
                     r.IsFailable && r.Name == "emit"))
        {
            if (!ctx.RoutineBodies.TryGetValue(key: routine.RegistryKey,
                    value: out Statement? body))
            {
                continue;
            }

            RegisterVariantsForEmitRoutine(routine: routine,
                body: body,
                generator: generator,
                pending: pending);
        }

        return pending;
    }

    /// <summary>
    /// Registers error-handling variants for a single eagerly-processed <c>emit</c> routine.
    /// Handles the <c>@crash_only</c> annotation case (analyze but suppress safe variants) and
    /// the normal case (register all generated variants and enqueue for body transformation).
    /// </summary>
    private void RegisterVariantsForEmitRoutine(RoutineInfo routine, Statement body,
        ErrorHandlingGenerator generator,
        List<(RoutineInfo routine, Statement body, List<GeneratedVariant> variants)> pending)
    {
        // @crash_only: still analyze throw/absent but suppress safe variant generation
        if (routine.Annotations.Contains(item: "crash_only"))
        {
            ErrorHandlingResult crashOnlyResult =
                generator.GenerateVariants(routine: routine, body: body);
            routine.HasThrow = crashOnlyResult.HasThrow;
            routine.HasAbsent = crashOnlyResult.HasAbsent;
            return;
        }

        ErrorHandlingResult result = generator.GenerateVariants(routine: routine, body: body);
        if (result.Error != null)
        {
            return;
        }

        routine.HasThrow = result.HasThrow;
        routine.HasAbsent = result.HasAbsent;
        routine.ThrowableTypes = result.ThrownTypes;

        foreach (RoutineInfo variantRoutine in result.Variants.Select(selector: v => v.Routine))
        {
            ctx.Registry.RegisterRoutine(routine: variantRoutine);
            variantRoutine.ThrowableTypes = result.ThrownTypes;
        }

        pending.Add(item: (routine, body, result.Variants));
    }

    /// <summary>
    /// Phase D: now that all variants are registered, transform each body — rewriter can
    /// find variants of inner failable calls and substitute them.
    /// </summary>
    private void TransformPendingBodies(
        List<(RoutineInfo routine, Statement body, List<GeneratedVariant> variants)> pending)
    {
        foreach ((RoutineInfo _, Statement body, List<GeneratedVariant> variants) in pending)
        {
            foreach (GeneratedVariant variant in variants)
            {
                ErrorHandlingVariantKind kind = DetermineVariantKind(variant: variant);
                Statement variantSourceBody = GenericAstRewriter.RewriteStatement(
                    stmt: body,
                    subs: new Dictionary<string, string>());
                Statement variantBody = TransformBody(body: variantSourceBody,
                    kind: kind,
                    rewriter: TryRewriteToVariantCall,
                    registry: ctx.Registry);
                // Warm-restore: a variant body pre-seeded from the captured stdlib is already lowered +
                // analyzed — keep it instead of overwriting with a fresh un-analyzed regeneration (the
                // seeded ones are what AnalyzeVariantBodies skips; overwriting would leave them unanalyzed).
                if (ctx.Registry.SkipStdlibReprocessing &&
                    ctx.VariantBodies.ContainsKey(key: variant.Routine.RegistryKey))
                {
                    continue;
                }

                ctx.VariantBodies[key: variant.Routine.RegistryKey] = variantBody;
            }
        }
    }

    /// <summary>
    /// Maps a <see cref="GeneratedVariant"/> to its <see cref="ErrorHandlingVariantKind"/>,
    /// including distinguishing the TryBool case (None-returning try_ variant).
    /// </summary>
    internal static ErrorHandlingVariantKind DetermineVariantKind(GeneratedVariant variant)
    {
        return variant.Kind switch
        {
            ErrorHandlingVariantKind.Try when variant.Routine.FailableVariant ==
                                              FailableVariant.TryBool => ErrorHandlingVariantKind
               .TryBool,
            _ => variant.Kind
        };
    }

    /// <summary>
    /// Builds ONE variant's body on demand (the same transform Phase D applies eagerly), for the
    /// SemanticVerifier's on-demand synthesizer. Uses the broad (path-1, <c>nextOnly:false</c>) propagation
    /// so inner failable calls are rewritten to THEIR try_/check_/lookup_ variants — those inner lookups go
    /// through <see cref="TypeRegistry.LookupMemberRoutine"/>, whose on-demand hook synthesizes the inner
    /// variant transitively.
    /// </summary>
    public static Statement GenerateVariantBody(Statement baseBody, GeneratedVariant variant,
        TypeRegistry registry)
    {
        ErrorHandlingVariantKind kind = DetermineVariantKind(variant: variant);
        Statement variantSourceBody = GenericAstRewriter.RewriteStatement(
            stmt: baseBody,
            subs: new Dictionary<string, string>());
        return TransformBody(body: variantSourceBody,
            kind: kind,
            rewriter: MakeOnDemandVariantRewriter(registry: registry),
            registry: registry,
            nextOnlyPropagation: false);
    }

    /// <summary>
    /// A tail-return rewriter for on-demand variant-body generation: identical to
    /// <see cref="MakeNextVariantRewriter"/> but NOT restricted to <c>emit</c> — it rewrites a tail call to
    /// ANY failable routine into its matching variant. The variant lookup goes through
    /// <see cref="TypeRegistry.LookupMemberRoutine"/>, so a not-yet-synthesized inner variant is created on
    /// the spot by the on-demand hook.
    /// </summary>
    public static VariantCallRewriter MakeOnDemandVariantRewriter(TypeRegistry registry)
    {
        // Find-or-SYNTHESIZE the matching variant of the SPECIFIC failable base overload via the verifier's
        // per-overload hook (matches by parameter types, so an overloaded base like S64.create(from_text:)
        // yields the right variant). Falls back to a plain name lookup when the hook isn't installed.
        // Demand-owned variants: synthesize the EXACT overload's variant via the per-overload hook.
        // Eager-owned (emit) or wired variants: the hook returns null, falling back to FindVariant — an
        // EXACT scan-match (name + OriginalName + owner + param types), never a lossy by-name lookup.
        RoutineInfo? FindOrSynth(RoutineInfo original, string prefix)
        {
            return registry.OnDemandVariantForBase?.Invoke(arg1: original, arg2: prefix) ??
                   FindVariant(registry: registry, original: original, prefix: prefix);
        }

        return (Expression? value, ErrorHandlingVariantKind kind, out Expression? rewritten) =>
        {
            rewritten = null;
            string? prefix = kind switch
            {
                ErrorHandlingVariantKind.Try => PrefixTry,
                ErrorHandlingVariantKind.Check => PrefixCheck,
                ErrorHandlingVariantKind.Lookup => PrefixLookup,
                _ => null
            };
            if (prefix == null)
            {
                return false;
            }

            if (TryRewriteCallToVariant(value: value,
                    prefix: prefix,
                    findOrSynth: FindOrSynth,
                    rewritten: out rewritten))
            {
                return true;
            }

            if (TryRewriteCreatorToVariant(value: value,
                    prefix: prefix,
                    findOrSynth: FindOrSynth,
                    rewritten: out rewritten))
            {
                return true;
            }

            return false;
        };
    }

    /// <summary>
    /// Rewrites a tail <see cref="CallExpression"/> whose resolved routine is failable into an
    /// equivalent call targeting the try_/check_/lookup_ variant. Returns false when the expression
    /// is not a failable call or the variant cannot be found.
    /// </summary>
    private static bool TryRewriteCallToVariant(Expression? value, string prefix,
        Func<RoutineInfo, string, RoutineInfo?> findOrSynth, out Expression? rewritten)
    {
        rewritten = null;
        if (value is not CallExpression { ResolvedRoutine: { IsFailable: true } callee } call)
        {
            return false;
        }

        RoutineInfo? variant = findOrSynth(arg1: callee, arg2: prefix);
        if (variant == null)
        {
            return false;
        }

        CallExpression newCall = call with
        {
            ResolvedRoutine = variant, ResolvedType = variant.ReturnType
        };
        newCall = newCall.Callee switch
        {
            MemberExpression m => newCall with
            {
                Callee = m with
                {
                    MemberName = VariantSurfaceMember(surfaceMember: m.MemberName,
                        original: callee,
                        variant: variant),
                    IsFailable = false
                }
            },
            IdentifierExpression idc => newCall with { Callee = idc with { Name = variant.Name } },
            _ => newCall
        };
        rewritten = newCall;
        return true;
    }

    /// <summary>
    /// Rewrites a tail failable constructor expression (e.g. T(from_text: t)) into a call to the
    /// corresponding try_/check_/lookup_ creator variant. Returns false when the expression is not a
    /// failable creator or the variant cannot be found.
    /// </summary>
    private static bool TryRewriteCreatorToVariant(Expression? value, string prefix,
        Func<RoutineInfo, string, RoutineInfo?> findOrSynth, out Expression? rewritten)
    {
        rewritten = null;
        if (value is not CreatorExpression
            {
                ResolvedCreatorRoutine: { IsFailable: true } cCallee
            } creator)
        {
            return false;
        }

        RoutineInfo? variant = findOrSynth(arg1: cCallee, arg2: prefix);
        if (variant == null)
        {
            return false;
        }

        var typeId = new IdentifierExpression(Name: creator.TypeName, Location: creator.Location);
        var member = new MemberExpression(Object: typeId,
            MemberName: variant.Name,
            Location: creator.Location);
        var args = creator.MemberVariables
                          .Select(selector: mv => (Expression)new NamedArgumentExpression(
                               Name: mv.Name,
                               Value: mv.Value,
                               Location: creator.Location))
                          .ToList();
        rewritten = new CallExpression(Callee: member, Arguments: args, Location: creator.Location)
        {
            ResolvedRoutine = variant, ResolvedType = variant.ReturnType
        };
        return true;
    }

    /// <summary>
    /// Signature for an optional rewriter that may convert a tail-return value into a passthrough
    /// call against the corresponding try_/check_/lookup_ variant of an inner failable callee.
    /// </summary>
    public delegate bool VariantCallRewriter(Expression? value, ErrorHandlingVariantKind kind,
        out Expression? rewritten);

    /// <summary>
    /// Recursively walks a routine body and replaces throw/absent/return statements with
    /// <see cref="VariantReturnStatement"/> nodes appropriate for the given variant kind.
    /// All other statements are passed through unchanged (structurally cloned via record-with).
    /// When <paramref name="rewriter"/> succeeds on a tail-position return value, the value is
    /// emitted as <see cref="VariantSiteKind.FromVariantPassthrough"/> so codegen returns the
    /// already-carrier-shaped expression directly.
    /// </summary>
    /// <param name="body">The routine body statement to transform.</param>
    /// <param name="kind">Which error-handling variant shape to produce (try/check/lookup/try-bool).</param>
    /// <param name="rewriter">Optional tail-return rewriter; when it succeeds the return is emitted as a passthrough.</param>
    /// <param name="registry">Optional type registry used for try_-variant synthesis when <paramref name="kind"/> is <c>Try</c>.</param>
    /// <param name="nextOnlyPropagation">
    /// When true, non-tail failable-call propagation is restricted to inner <c>emit</c> calls
    /// (iterator chaining). Used by the MONOMORPHIZED (path-2) caller, which runs AFTER reachability:
    /// any other <c>try_X</c> it introduces wouldn't be marked live and would LINKERR (e.g. a guarded
    /// <c>getitem!</c>), whereas <c>try_emit</c> is always emitted for live iterators. When false
    /// (the global path-1 caller, which runs BEFORE reachability), ALL non-tail failable calls are
    /// propagated — reachability then sees the introduced <c>try_X</c> calls and emits them. Path-1
    /// MUST propagate broadly so a try_ variant whose failability is purely propagated through a
    /// non-tail call (e.g. <c>try_from_digit_bytes</c> → <c>from_digit_bytes_at!</c>) actually catches
    /// the inner throw/absent instead of letting it escape uncaught.
    /// </param>
    internal static Statement TransformBody(Statement body, ErrorHandlingVariantKind kind,
        VariantCallRewriter? rewriter = null, TypeRegistry? registry = null,
        bool nextOnlyPropagation = false)
    {
        TypeRegistry? propRegistry = registry != null && kind == ErrorHandlingVariantKind.Try
            ? registry
            : null;
        return TransformBodyCore(body: body,
            kind: kind,
            rewriter: rewriter,
            registry: propRegistry,
            nextOnly: nextOnlyPropagation);
    }

    private static Statement TransformBodyCore(Statement body, ErrorHandlingVariantKind kind,
        VariantCallRewriter? rewriter, TypeRegistry? registry, bool nextOnly)
    {
        return body switch
        {
            // `pierce` stays a crash even inside a try_/check_ variant — it pierces through the
            // recovery surface, so it is NOT rewritten into a recoverable return.
            ThrowStatement { IsFatal: true } => body,

            ThrowStatement ts => new VariantReturnStatement(VariantKind: kind,
                SiteKind: VariantSiteKind.FromThrow,
                Value: ts.Error,
                Location: ts.Location),

            AbsentStatement abs => new VariantReturnStatement(VariantKind: kind,
                SiteKind: VariantSiteKind.FromAbsent,
                Value: null,
                Location: abs.Location),

            ReturnStatement ret when rewriter != null && rewriter(value: ret.Value,
                kind: kind,
                rewritten: out Expression? vcall) => new VariantReturnStatement(VariantKind: kind,
                SiteKind: VariantSiteKind.FromVariantPassthrough,
                Value: vcall,
                Location: ret.Location),

            ReturnStatement ret => new VariantReturnStatement(VariantKind: kind,
                SiteKind: VariantSiteKind.FromReturn,
                Value: ret.Value,
                Location: ret.Location),

            BlockStatement block => block with
            {
                Statements = TransformBlockStatements(stmts: block.Statements,
                    start: 0,
                    kind: kind,
                    rewriter: rewriter,
                    registry: registry,
                    nextOnly: nextOnly)
            },

            IfStatement ifs => TransformIf(ifs: ifs,
                kind: kind,
                rewriter: rewriter,
                registry: registry,
                nextOnly: nextOnly),
            WhileStatement ws => TransformWhile(ws: ws,
                kind: kind,
                rewriter: rewriter,
                registry: registry,
                nextOnly: nextOnly),
            EachStatement fs => TransformEach(fs: fs,
                kind: kind,
                rewriter: rewriter,
                registry: registry,
                nextOnly: nextOnly),

            WhenStatement ws => ws with
            {
                Clauses = ws.Clauses
                            .Select(selector: c => c with
                             {
                                 Body = TransformBodyCore(body: c.Body,
                                     kind: kind,
                                     rewriter: rewriter,
                                     registry: registry,
                                     nextOnly: nextOnly)
                             })
                            .ToList()
            },

            UsingStatement us => TransformUsing(us: us,
                kind: kind,
                rewriter: rewriter,
                registry: registry,
                nextOnly: nextOnly),

            DangerStatement danger => danger with
            {
                Body = (BlockStatement)TransformBodyCore(body: danger.Body,
                    kind: kind,
                    rewriter: rewriter,
                    registry: registry,
                    nextOnly: nextOnly)
            },

            LoopStatement loop => loop with
            {
                Body = TransformBodyCore(body: loop.Body,
                    kind: kind,
                    rewriter: rewriter,
                    registry: registry,
                    nextOnly: nextOnly)
            },

            _ => body // All other statements pass through unchanged
        };
    }

    private static IfStatement TransformIf(IfStatement ifs, ErrorHandlingVariantKind kind,
        VariantCallRewriter? rewriter, TypeRegistry? registry, bool nextOnly)
    {
        return ifs with
        {
            ThenStatement =
            TransformBodyCore(body: ifs.ThenStatement,
                kind: kind,
                rewriter: rewriter,
                registry: registry,
                nextOnly: nextOnly),
            ElseStatement = ifs.ElseStatement != null
                ? TransformBodyCore(body: ifs.ElseStatement,
                    kind: kind,
                    rewriter: rewriter,
                    registry: registry,
                    nextOnly: nextOnly)
                : null
        };
    }

    private static WhileStatement TransformWhile(WhileStatement ws, ErrorHandlingVariantKind kind,
        VariantCallRewriter? rewriter, TypeRegistry? registry, bool nextOnly)
    {
        return ws with
        {
            Body = TransformBodyCore(body: ws.Body,
                kind: kind,
                rewriter: rewriter,
                registry: registry,
                nextOnly: nextOnly),
            ElseBranch = ws.ElseBranch != null
                ? TransformBodyCore(body: ws.ElseBranch,
                    kind: kind,
                    rewriter: rewriter,
                    registry: registry,
                    nextOnly: nextOnly)
                : null
        };
    }

    private static EachStatement TransformEach(EachStatement fs, ErrorHandlingVariantKind kind,
        VariantCallRewriter? rewriter, TypeRegistry? registry, bool nextOnly)
    {
        return fs with
        {
            Body = TransformBodyCore(body: fs.Body,
                kind: kind,
                rewriter: rewriter,
                registry: registry,
                nextOnly: nextOnly),
            ElseBranch = fs.ElseBranch != null
                ? TransformBodyCore(body: fs.ElseBranch,
                    kind: kind,
                    rewriter: rewriter,
                    registry: registry,
                    nextOnly: nextOnly)
                : null
        };
    }

    private static UsingStatement TransformUsing(UsingStatement us, ErrorHandlingVariantKind kind,
        VariantCallRewriter? rewriter, TypeRegistry? registry, bool nextOnly)
    {
        return us with
        {
            Body = TransformBodyCore(body: us.Body,
                kind: kind,
                rewriter: rewriter,
                registry: registry,
                nextOnly: nextOnly),
            FallbackBody = us.FallbackBody != null
                ? TransformBodyCore(body: us.FallbackBody,
                    kind: kind,
                    rewriter: rewriter,
                    registry: registry,
                    nextOnly: nextOnly)
                : null
        };
    }

    private static int _propTemp;

    /// <summary>
    /// Resolves the <paramref name="prefix"/> (try/check/lookup) variant of the SPECIFIC base overload
    /// <paramref name="original"/> on <paramref name="owner"/>. Routes through the per-overload on-demand
    /// synthesizer FIRST (<see cref="TypeRegistry.OnDemandVariantForBase"/>): it returns — synthesizing on
    /// demand when needed — the variant OF THIS overload, whose parameter types match <paramref name="original"/>
    /// by construction. A bare name lookup is wrong for a heavily-overloaded base (e.g. <c>U32.create!</c>
    /// from S8/S16/S32/S64/…): it returns an arbitrary <c>try_create</c> (the first-registered S8) whose
    /// parameter type mismatches the call's argument, producing invalid IR (a <c>try_create(from: S8)</c>
    /// fed an i64). Falls back to an overload-typed lookup, then a name-only lookup, when the hook is
    /// absent or a parameter type isn't a concrete <see cref="TypeInfo"/>.
    /// </summary>
    private static RoutineInfo? LookupVariantForOverload(TypeRegistry registry, TypeInfo owner,
        string prefix, RoutineInfo original)
    {
        RoutineInfo? synth = registry.OnDemandVariantForBase?.Invoke(arg1: original, arg2: prefix);
        if (synth != null)
        {
            return synth;
        }

        string variantName = $"{prefix}_{original.OriginalName ?? original.Name}";
        var argTypes = new List<TypeInfo>();
        foreach (ParameterInfo p in original.Parameters)
        {
            if (p.Type is TypeInfo ti)
            {
                argTypes.Add(item: ti);
            }
            else
            {
                return registry.LookupMemberRoutine(type: owner,
                    memberRoutineName: variantName,
                    isFailable: false);
            }
        }

        return registry.LookupMemberRoutineOverload(type: owner,
            memberRoutineName: variantName,
            argTypes: argTypes) ?? registry.LookupMemberRoutine(type: owner,
            memberRoutineName: variantName,
            isFailable: false);
    }

    /// <summary>
    /// Transforms a block's statements, propagating NON-tail failable calls through their safe
    /// variant. The tail-position <paramref name="rewriter"/> only handles <c>return F!(x)</c>; a
    /// failable call used in statement position — e.g. <c>var item = src.emit!()</c> — would
    /// otherwise be left calling the raw <c>!</c> routine, which HARD-CRASHES on absence (the raw
    /// form lowers <c>absent</c> to <c>rf_crash</c>). Inside a <c>try_</c> variant that inner
    /// absence must instead become this variant's own <c>None</c> return.
    ///
    /// For each such statement the remainder of the block is folded into the success branch of a
    /// plain <c>if</c> over the inner safe variant's Maybe carrier:
    /// <code>
    /// var __rf_prop_N = src.try_emit()      # Maybe[T]
    /// if __rf_prop_N.present
    ///   var item = __rf_prop_N.value
    ///   &lt;rest of block&gt;
    /// else
    ///   &lt;return None&gt;                       # VariantReturnStatement(Try, FromAbsent)
    /// </code>
    /// Only the <c>if</c>, Bool field-read and field access are used — all codegen-ready without
    /// pattern/operator lowering, so this works in BOTH the global variant path (which has
    /// downstream lowering) and the monomorphized fallback path (which does not). Scoped to the
    /// <c>Try</c> kind: only the <c>Maybe</c> carrier has the flat <c>{present,value}</c> layout this
    /// unwrap relies on; Check/Lookup carriers keep the existing tail-position behavior.
    /// </summary>
    private static List<Statement> TransformBlockStatements(List<Statement> stmts, int start,
        ErrorHandlingVariantKind kind, VariantCallRewriter? rewriter, TypeRegistry? registry,
        bool nextOnly)
    {
        var result = new List<Statement>();
        for (int i = start; i < stmts.Count; i++)
        {
            Statement s = stmts[index: i];

            if (kind == ErrorHandlingVariantKind.Try && registry != null && TryBuildTryPropagation(
                    stmt: s,
                    registry: registry,
                    nextOnly: nextOnly,
                    tempDecl: out Statement? tempDecl,
                    presentCondition: out Expression? presentCondition,
                    bindStmt: out Statement? bindStmt))
            {
                List<Statement> remainder = TransformBlockStatements(stmts: stmts,
                    start: i + 1,
                    kind: kind,
                    rewriter: rewriter,
                    registry: registry,
                    nextOnly: nextOnly);

                var thenStmts = new List<Statement>();
                if (bindStmt != null)
                {
                    thenStmts.Add(item: bindStmt);
                }

                thenStmts.AddRange(collection: remainder);

                result.Add(item: tempDecl!);
                result.Add(item: new IfStatement(Condition: presentCondition!,
                    ThenStatement: new BlockStatement(Statements: thenStmts, Location: s.Location),
                    ElseStatement: new VariantReturnStatement(VariantKind: kind,
                        SiteKind: VariantSiteKind.FromAbsent,
                        Value: null,
                        Location: s.Location),
                    Location: s.Location));
                return result; // remainder consumed into the if's then-branch
            }

            // Check/Lookup variants: Result/Lookup carriers are tag-based (not the flat {present,value}
            // of Maybe), so propagate a non-tail failable call through a `when` over the inner's
            // same-kind variant. Only in the global path-1 (`!nextOnly`): the synthesized `when`
            // (incl. `is Crashable`) is lowered by CrashableExpansionPass + PatternLoweringPass which
            // run on path-1 variant bodies but NOT on path-2 monomorphized bodies.
            if (kind is ErrorHandlingVariantKind.Check or ErrorHandlingVariantKind.Lookup &&
                registry != null && !nextOnly && TryBuildCarrierSafeCall(stmt: s,
                    registry: registry,
                    kind: kind,
                    safeCall: out Expression? carrierCall,
                    bindName: out string? carrierBind,
                    innerCanNone: out bool innerCanNone,
                    innerCanError: out bool innerCanError))
            {
                List<Statement> remainder = TransformBlockStatements(stmts: stmts,
                    start: i + 1,
                    kind: kind,
                    rewriter: rewriter,
                    registry: registry,
                    nextOnly: nextOnly);
                result.Add(item: BuildCarrierPropagationWhen(subject: carrierCall!,
                    bindName: carrierBind,
                    kind: kind,
                    innerCanNone: innerCanNone,
                    innerCanError: innerCanError,
                    remainder: remainder,
                    loc: s.Location));
                return result; // remainder consumed into the when's success arm
            }

            result.Add(item: TransformBodyCore(body: s,
                kind: kind,
                rewriter: rewriter,
                registry: registry,
                nextOnly: nextOnly));
        }

        return result;
    }

    /// <summary>
    /// If <paramref name="stmt"/> uses a failable call in non-tail position (a <c>var x = F!(...)</c>
    /// declaration or a bare <c>F!(...)</c> expression statement) and the callee has a <c>try_</c>
    /// variant returning a <c>Maybe</c>, produces the spliced pieces:
    /// <list type="bullet">
    /// <item><paramref name="tempDecl"/>: <c>var __rf_prop_N = recv.try_X(...)</c></item>
    /// <item><paramref name="presentCondition"/>: <c>__rf_prop_N.present</c> (the <c>if</c> condition)</item>
    /// <item><paramref name="bindStmt"/>: <c>var x = __rf_prop_N.value</c> (null when the result was discarded)</item>
    /// </list>
    /// Returns false — leaving the original crash-on-absence statement untouched — when no matching
    /// Maybe-returning <c>try_</c> variant resolves.
    /// </summary>
    private static bool TryBuildTryPropagation(Statement stmt, TypeRegistry registry,
        bool nextOnly, out Statement? tempDecl, out Expression? presentCondition,
        out Statement? bindStmt)
    {
        tempDecl = null;
        presentCondition = null;
        bindStmt = null;

        CallExpression failCall;
        string? bindName;
        switch (stmt)
        {
            case DeclarationStatement
            {
                Declaration: VariableDeclaration { Initializer: CallExpression ce } vd
            } when ce.ResolvedRoutine is { IsFailable: true }:
                failCall = ce;
                bindName = vd.Name;
                break;
            case ExpressionStatement { Expression: CallExpression ce2 }
                when ce2.ResolvedRoutine is { IsFailable: true }:
                failCall = ce2;
                bindName = null;
                break;
            default:
                return false;
        }

        RoutineInfo failRoutine = failCall.ResolvedRoutine;
        if (failRoutine.OwnerType is not { } owner)
        {
            return false;
        }

        string baseName = failRoutine.OriginalName ?? failRoutine.Name;

        // In the monomorphized (path-2) caller — which runs AFTER reachability — restrict propagation
        // to inner `emit!` calls: `try_emit` is systematically emitted for live iterator instances, so
        // the propagated chain always links, whereas an arbitrary `try_X` introduced here wouldn't be
        // marked live and would LINKERR (e.g. a bounds-guarded `getitem!` in SortedSetIterator, which
        // also can't actually fail). The global (path-1) caller runs BEFORE reachability, so it
        // propagates ALL non-tail failable calls and reachability then emits the introduced variants.
        if (nextOnly && baseName != "emit")
        {
            return false;
        }

        RoutineInfo? variant = LookupVariantForOverload(registry: registry,
            owner: owner,
            prefix: PrefixTry,
            original: failRoutine);

        // Need a Maybe carrier (flat {present,value}) to unwrap with field access. The TryBool
        // variant returns Bool (no type args) and is rejected here.
        if (variant?.ReturnType is not { TypeArguments.Count: > 0 } carrier)
        {
            return false;
        }

        SourceLocation loc = stmt.Location;
        string tempName = $"__rf_prop_{Interlocked.Increment(location: ref _propTemp)}";

        // Retarget the failable call to its try_ variant and re-type it as the carrier.
        CallExpression safeCall =
            failCall with { ResolvedRoutine = variant, ResolvedType = carrier };
        safeCall = safeCall.Callee switch
        {
            MemberExpression m => safeCall with
            {
                Callee = m with
                {
                    MemberName = VariantSurfaceMember(surfaceMember: m.MemberName,
                        original: failCall.ResolvedRoutine,
                        variant: variant),
                    IsFailable = false
                }
            },
            IdentifierExpression idc => safeCall with
            {
                Callee = idc with { Name = variant.Name }
            },
            _ => safeCall
        };

        tempDecl = new DeclarationStatement(Declaration: new VariableDeclaration(Name: tempName,
                Type: null,
                Initializer: safeCall,
                Visibility: VisibilityModifier.Secret,
                Location: loc),
            Location: loc);

        presentCondition = new MemberExpression(
            Object: new IdentifierExpression(Name: tempName, Location: loc)
            {
                ResolvedType = carrier
            },
            MemberName: RuntimeContract.Carrier.PresentField,
            Location: loc);

        if (bindName != null)
        {
            TypeInfo? valueType = carrier.TypeArguments[index: 0];
            Expression valueAccess = new MemberExpression(
                Object: new IdentifierExpression(Name: tempName, Location: loc)
                {
                    ResolvedType = carrier
                },
                MemberName: RuntimeContract.Carrier.ValueField,
                Location: loc) { ResolvedType = valueType };

            bindStmt = new DeclarationStatement(
                Declaration: new VariableDeclaration(Name: bindName,
                    Type: null,
                    Initializer: valueAccess,
                    Visibility: VisibilityModifier.Secret,
                    Location: loc),
                Location: loc);
        }

        return true;
    }

    /// <summary>
    /// Like <see cref="TryBuildTryPropagation"/> but for Check/Lookup variants: retargets a non-tail
    /// failable call to the BEST available inner safe variant and reports what that carrier can fail
    /// with. The inner routine may not have the outer's exact kind — variant generation produces
    /// try_ only (absent-only), try_+check_ (throw-only), or try_+lookup_ (both). So fall back:
    /// prefer the outer's kind, then lookup_ &gt; check_ &gt; try_ (try_ always exists). The chosen
    /// carrier's capabilities (<paramref name="innerCanNone"/>/<paramref name="innerCanError"/>)
    /// drive which arms <see cref="BuildCarrierPropagationWhen"/> emits. Returns false when no failure
    /// arm would apply (e.g. a Check outer over an absent-only inner — an inconsistent combination),
    /// leaving the original statement untouched.
    /// </summary>
    private static bool TryBuildCarrierSafeCall(Statement stmt, TypeRegistry registry,
        ErrorHandlingVariantKind kind, out Expression? safeCall, out string? bindName,
        out bool innerCanNone, out bool innerCanError)
    {
        safeCall = null;
        bindName = null;
        innerCanNone = false;
        innerCanError = false;

        CallExpression failCall;
        switch (stmt)
        {
            case DeclarationStatement
            {
                Declaration: VariableDeclaration { Initializer: CallExpression ce } vd
            } when ce.ResolvedRoutine is { IsFailable: true }:
                failCall = ce;
                bindName = vd.Name;
                break;
            case ExpressionStatement { Expression: CallExpression ce2 }
                when ce2.ResolvedRoutine is { IsFailable: true }:
                failCall = ce2;
                bindName = null;
                break;
            default:
                return false;
        }

        RoutineInfo failRoutine = failCall.ResolvedRoutine;
        if (failRoutine.OwnerType is not { } owner)
        {
            return false;
        }

        // Prefer the outer kind's variant, then fall back to the most-informative available.
        string[] order = kind == ErrorHandlingVariantKind.Check
            ? [PrefixCheck, PrefixLookup, PrefixTry]
            : [PrefixLookup, PrefixCheck, PrefixTry];
        RoutineInfo? variant = null;
        string chosen = "";
        foreach (string p in order)
        {
            RoutineInfo? v = LookupVariantForOverload(registry: registry,
                owner: owner,
                prefix: p,
                original: failRoutine);
            if (v?.ReturnType is { TypeArguments.Count: > 0 })
            {
                variant = v;
                chosen = p;
                break;
            }
        }

        if (variant?.ReturnType is not { } carrier)
        {
            return false;
        }

        innerCanNone = chosen is PrefixTry or PrefixLookup;
        innerCanError = chosen is PrefixCheck or PrefixLookup;

        // The outer carrier represents None only for Lookup (Try uses the flat-field path), and an
        // error for both Check and Lookup. If neither failure the inner can produce maps onto the
        // outer, propagation is meaningless — leave the call raw.
        bool outerCanNone = kind == ErrorHandlingVariantKind.Lookup;
        if (!(innerCanNone && outerCanNone || innerCanError))
        {
            return false;
        }

        CallExpression retargeted =
            failCall with { ResolvedRoutine = variant, ResolvedType = carrier };
        retargeted = retargeted.Callee switch
        {
            MemberExpression m => retargeted with
            {
                Callee = m with
                {
                    MemberName = VariantSurfaceMember(surfaceMember: m.MemberName,
                        original: failCall.ResolvedRoutine,
                        variant: variant),
                    IsFailable = false
                }
            },
            IdentifierExpression idc => retargeted with
            {
                Callee = idc with { Name = variant.Name }
            },
            _ => retargeted
        };
        safeCall = retargeted;
        return true;
    }

    /// <summary>
    /// Builds the <c>when</c> that short-circuits a Check/Lookup variant on the inner carrier's
    /// failure and otherwise binds the unwrapped success value before running the remainder:
    /// <code>
    /// when inner.&lt;safe&gt;_x()
    ///   is None -&gt; &lt;return None&gt;          # inner can None AND outer is Lookup
    ///   is Crashable e -&gt; &lt;return error e&gt; # inner can error (Check/Lookup outer)
    ///   else var x -&gt; &lt;remainder&gt;
    /// </code>
    /// Arms are emitted only for failures the chosen inner carrier can produce AND the outer can
    /// represent. Lowered by CrashableExpansionPass + PatternLoweringPass on path-1 variant bodies.
    /// </summary>
    private static WhenStatement BuildCarrierPropagationWhen(Expression subject, string? bindName,
        ErrorHandlingVariantKind kind, bool innerCanNone, bool innerCanError,
        List<Statement> remainder, SourceLocation loc)
    {
        var clauses = new List<WhenClause>();

        if (innerCanNone && kind == ErrorHandlingVariantKind.Lookup)
        {
            clauses.Add(item: new WhenClause(Pattern: new NonePattern(Location: loc),
                Body: new VariantReturnStatement(VariantKind: kind,
                    SiteKind: VariantSiteKind.FromAbsent,
                    Value: null,
                    Location: loc),
                Location: loc));
        }

        if (innerCanError)
        {
            const string errName = "__rf_prop_err";
            clauses.Add(item: new WhenClause(
                Pattern: new CrashablePattern(ErrorType: null,
                    VariableName: errName,
                    Location: loc),
                Body: new VariantReturnStatement(VariantKind: kind,
                    SiteKind: VariantSiteKind.FromThrow,
                    Value: new IdentifierExpression(Name: errName, Location: loc),
                    Location: loc),
                Location: loc));
        }

        clauses.Add(item: new WhenClause(
            Pattern: new ElsePattern(VariableName: bindName, Location: loc),
            Body: new BlockStatement(Statements: remainder, Location: loc),
            Location: loc));

        return new WhenStatement(Expression: subject, Clauses: clauses, Location: loc);
    }

    /// <summary>
    /// Builds a registry-based <see cref="VariantCallRewriter"/> for the monomorphized fallback path
    /// (<see cref="Compiler.Instantiation.Passes.GenericMonomorphizationPass"/>), which has no
    /// per-pass rewriter instance. It rewrites a TAIL-position <c>return src.emit!()</c> into a
    /// passthrough call to the matching <c>try_/check_/lookup_emit</c> variant (resolved via
    /// <see cref="TypeRegistry.LookupMemberRoutine"/> on the concrete callee owner). Restricted to
    /// <c>emit</c> for the same reason as <see cref="TryBuildTryPropagation"/>: <c>try_emit</c> is
    /// systematically emitted for live iterator instances, so the rewritten chain always links.
    /// </summary>
    public static VariantCallRewriter MakeNextVariantRewriter(TypeRegistry registry)
    {
        return (Expression? value, ErrorHandlingVariantKind kind, out Expression? rewritten) =>
        {
            rewritten = null;
            string? prefix = kind switch
            {
                ErrorHandlingVariantKind.Try => PrefixTry,
                ErrorHandlingVariantKind.Check => PrefixCheck,
                ErrorHandlingVariantKind.Lookup => PrefixLookup,
                _ => null
            };
            if (prefix == null)
            {
                return false;
            }

            if (value is not CallExpression { ResolvedRoutine: { IsFailable: true } callee } call)
            {
                return false;
            }

            string baseName = callee.OriginalName ?? callee.Name;
            if (baseName != "emit")
            {
                return false;
            }

            if (callee.OwnerType is not { } owner)
            {
                return false;
            }

            RoutineInfo? variant = registry.LookupMemberRoutine(type: owner,
                memberRoutineName: $"{prefix}_{baseName}",
                isFailable: false);
            if (variant == null)
            {
                return false;
            }

            CallExpression newCall = call with
            {
                ResolvedRoutine = variant, ResolvedType = variant.ReturnType
            };
            newCall = newCall.Callee switch
            {
                MemberExpression m => newCall with
                {
                    Callee = m with
                    {
                        MemberName = VariantSurfaceMember(surfaceMember: m.MemberName,
                            original: callee,
                            variant: variant),
                        IsFailable = false
                    }
                },
                IdentifierExpression idc => newCall with
                {
                    Callee = idc with { Name = variant.Name }
                },
                _ => newCall
            };
            rewritten = newCall;
            return true;
        };
    }

    /// <summary>
    /// If <paramref name="value"/> is a tail-position call to a failable routine and a matching
    /// variant exists in the registry, returns a rewritten call that targets the variant.
    /// The rewritten call's resolved routine is the variant (non-failable) so codegen does not
    /// emit a throw-propagating call site.
    /// </summary>
    private bool TryRewriteToVariantCall(Expression? value, ErrorHandlingVariantKind kind,
        out Expression? rewritten)
    {
        rewritten = null;
        if (value == null)
        {
            return false;
        }

        string? prefix = kind switch
        {
            ErrorHandlingVariantKind.Try => PrefixTry,
            ErrorHandlingVariantKind.Check => PrefixCheck,
            ErrorHandlingVariantKind.Lookup => PrefixLookup,
            _ => null
        };
        if (prefix == null)
        {
            return false;
        }

        if (value is CallExpression { ResolvedRoutine: { IsFailable: true } callee } call)
        {
            RoutineInfo? variant =
                FindVariant(registry: ctx.Registry, original: callee, prefix: prefix);
            if (variant == null)
            {
                return false;
            }

            // The passthrough value IS the variant's carrier (e.g. Maybe[S64]); record that type so
            // downstream (teardown return-spill, codegen) sizes slots from the carrier, not the
            // original unwrapped payload (S64) — a mismatch otherwise yields `store i64 %maybeVal`.
            CallExpression newCall = call with
            {
                ResolvedRoutine = variant, ResolvedType = variant.ReturnType
            };
            newCall = newCall.Callee switch
            {
                IdentifierExpression idCallee => newCall with
                {
                    Callee = idCallee with { Name = variant.Name }
                },
                MemberExpression memCallee => newCall with
                {
                    Callee = memCallee with
                    {
                        MemberName =
                        VariantSurfaceMember(surfaceMember: memCallee.MemberName,
                            original: callee,
                            variant: variant),
                        IsFailable = false
                    }
                },
                _ => newCall
            };
            rewritten = newCall;
            return true;
        }

        if (value is CreatorExpression
            {
                ResolvedCreatorRoutine: { IsFailable: true } cCallee
            } creator)
        {
            RoutineInfo? variant =
                FindVariant(registry: ctx.Registry, original: cCallee, prefix: prefix);
            if (variant == null)
            {
                return false;
            }

            var typeId =
                new IdentifierExpression(Name: creator.TypeName, Location: creator.Location);
            var member = new MemberExpression(Object: typeId,
                MemberName: variant.Name,
                Location: creator.Location);
            var args = creator.MemberVariables
                              .Select(selector: mv => (Expression)new NamedArgumentExpression(
                                   Name: mv.Name,
                                   Value: mv.Value,
                                   Location: creator.Location))
                              .ToList();
            var newCall =
                new CallExpression(Callee: member, Arguments: args, Location: creator.Location)
                {
                    ResolvedRoutine = variant, ResolvedType = variant.ReturnType
                };
            rewritten = newCall;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Finds the variant routine for <paramref name="original"/> with the given prefix
    /// (try/check/lookup). Matches by <see cref="RoutineInfo.OriginalName"/> + owner identity.
    /// </summary>
    /// <summary>
    /// The SURFACE member name a variant-retargeted call must carry so re-analysis re-resolves it to the
    /// SAME routine. For a normal call the surface member equals the routine's base name, so this is just
    /// <c>variant.Name</c> (<c>getitem</c> → <c>try_getitem</c>). For a CONVERSION chain the surface member
    /// is a TYPE name that differs from the routine name (<c>.S64!()</c>: surface <c>S64</c>, routine
    /// <c>create</c>) — there the variant surface must be <c>{prefix}_{surfaceMember}</c> (<c>try_S64</c>),
    /// NOT <c>variant.Name</c> (<c>try_create</c>): SA's conversion resolution maps <c>.try_S64()</c> →
    /// <c>S64.try_create</c> (the TARGET), whereas bare <c>try_create</c> re-resolves against the RECEIVER
    /// type (<c>F64.try_create</c>) and corrupts the binding. Prefix is recovered from <c>variant.Name</c>.
    /// </summary>
    private static string VariantSurfaceMember(string surfaceMember, RoutineInfo original,
        RoutineInfo variant)
    {
        string baseName = original.OriginalName ?? original.Name;
        if (surfaceMember == baseName)
        {
            return variant.Name;
        }

        if (variant.Name.EndsWith(value: "_" + baseName, comparisonType: StringComparison.Ordinal))
        {
            return $"{variant.Name[..^(baseName.Length + 1)]}_{surfaceMember}";
        }

        return variant.Name;
    }

    internal static RoutineInfo? FindVariant(TypeRegistry registry, RoutineInfo original,
        string prefix)
    {
        string baseName = original.Name;
        string variantName = $"{prefix}_{baseName}";
        foreach (RoutineInfo r in registry.GetAllRoutines())
        {
            if (r.Name != variantName)
            {
                continue;
            }

            if (r.OriginalName != original.Name)
            {
                continue;
            }

            if (!ReferenceEquals(objA: r.OwnerType, objB: original.OwnerType))
            {
                continue;
            }

            if (!ParametersMatch(candidate: r, original: original))
            {
                continue;
            }

            return r;
        }

        return null;
    }

    /// <summary>
    /// Returns true when <paramref name="candidate"/> has the same parameter count as
    /// <paramref name="original"/> and every parameter's <c>FullName</c> matches positionally.
    /// </summary>
    private static bool ParametersMatch(RoutineInfo candidate, RoutineInfo original)
    {
        if (candidate.Parameters.Count != original.Parameters.Count)
        {
            return false;
        }

        for (int i = 0; i < candidate.Parameters.Count; i++)
        {
            if (candidate.Parameters[index: i].Type.FullName !=
                original.Parameters[index: i].Type.FullName)
            {
                return false;
            }
        }

        return true;
    }
}
