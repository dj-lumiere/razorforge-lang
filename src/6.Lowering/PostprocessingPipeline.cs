using Compiler.Desugaring.Passes;
using Compiler.Lowering.Passes;
using SyntaxTree;
using Compiler.Declaration;

namespace Compiler.Lowering;

/// <summary>
/// Phase 8 pipeline: type-aware lowering on already-verified AST.
/// Runs after Phase 5 (semantic analysis) and Phase 6 synthesis.
/// </summary>
public sealed class PostprocessingPipeline(PostprocessingContext ctx)
{
    /// <summary>
    /// Runs all Phase 8 lowering passes on a single program (user file or stdlib file).
    /// Must be called after SA has annotated ResolvedType on all expressions.
    /// </summary>
    public void Run(Program program)
    {
        // Route Suflae module-level `global`s through the hidden __ModuleGlobals entity FIRST, so every
        // subsequent pass (f-string, operator, Roamed projection/lock-bracket) sees the field accesses
        // and the globals inherit the entity's thread-safe access-lock brackets.
        new GlobalEntityRewritePass(ctx: ctx).Run(program: program);
        new VariantReturnLoweringPass(ctx: ctx).Run(program: program);
        new LiteralLoweringPass(ctx: ctx).Run(program: program);
        new BuilderQueryInliningPass(registry: ctx.Registry, variantBodies: ctx.VariantBodies).Run(
            program: program);
        new GenericCallLoweringPass(registry: ctx.Registry, variantBodies: ctx.VariantBodies).Run(
            program: program);
        StructuralLoweringPass.Run(program: program);
        new FStringLoweringPass(ctx: ctx).Run(program: program);
        new CrashableExpansionPass(ctx: ctx).Run(program: program);
        // CallOverloadResolutionPass runs after FStringLoweringPass so that the represent/diagnose
        // calls it synthesizes are visible and can be classified before reaching codegen.
        new CallOverloadResolutionPass(ctx: ctx).Run(program: program);
        // PatternLowering before ExpressionLowering: PLP introduces UnaryExpression(Not)
        // when lowering WhenStatement -> IfStatement chains; ELP must see those new nodes.
        // OLP runs after ELP so chained comparisons are already split into BinaryExpressions.
        new PatternLoweringPass(ctx: ctx).Run(program: program);
        var elp = new ExpressionLoweringPass(ctx: ctx);
        elp.Run(program: program);
        // ExpressionLoweringPass synthesizes WhenStatements with NonePattern / TypePattern("None")
        // when lowering `??` and `?.` (see MakeAbsencePattern), and hoists when-expressions into
        // WhenStatements. Those are inserted AFTER the first PatternLoweringPass run, so re-run PLP
        // to fold them into the if/else chains codegen expects. That second PLP run can introduce
        // UnaryExpression(Not) (e.g. `not present`) on Maybe[T record] absence checks, so re-run ELP
        // after it to lower those into ConditionalExpression form. When the first ELP produced NO
        // WhenStatement, this whole round is a pure no-op re-walk — skip it.
        if (elp.ProducedWhenStatement)
        {
            new PatternLoweringPass(ctx: ctx).Run(program: program);
            new ExpressionLoweringPass(ctx: ctx).Run(program: program);
        }

        new OperatorLoweringPass(ctx: ctx).Run(program: program);
        // RoamedProjectionLoweringPass runs after OperatorLoweringPass and FStringLoweringPass so it
        // sees the operator/f-string-lowered Roamed receiver calls; it rewrites the codegen-side
        // raw_inner() projection into a real AST call (+ inner represent/diagnose re-resolution).
        new RoamedProjectionLoweringPass(ctx: ctx).Run(program: program);
        // Moves the Stage 2b spawn-boundary `promote()` out of codegen into a real AST call: for each
        // Roamed[T] argument of a suspended/threaded spawn, inserts `arg.promote()` before the spawn.
        new RoamedSpawnPromotionLoweringPass(ctx: ctx).Run(program: program);
        new RecordCopyLoweringPass(ctx: ctx).Run(program: program);
        // NOTE: RcRetainLoweringPass (the RC-simulation pass that injected a per-RC-field retain bump
        // at every record-copy site) is DELETED. The RC increment on a value copy already lives in the
        // type's own `assign`/`copy` DERIVE (BuildRecordCopyBody field-walks `me.f.assign()`); the pass
        // bumped ON TOP of that → double-count → teardown double-free. The RC decrement is handled
        // wholesale by scope-exit teardown (ScopeTeardownLoweringPass), which also destroys the old
        // value on a local reassignment. Field-target reassignment release-old (the pass's other job)
        // is a follow-up for ScopeTeardownLoweringPass's member-target case.
        // Moves the Roamed[E] access-lock bracket (lock_enter/lock_exit around a direct entity-field
        // read/write) out of codegen into real AST calls: one enter before / one exit after each
        // statement that touches a Roamed field. Runs after the other Roamed passes so field accesses
        // are in final form.
        new RoamedLockBracketLoweringPass(ctx: ctx).Run(program: program);
        new BecomesLoweringPass(_: ctx).Run(program: program);
        new UsingLoweringPass(ctx: ctx).Run(program: program);
        new LambdaLiftingPass(ctx: ctx).Run(program: program);
    }

    /// <summary>
    /// Runs Phase 8 lowering on variant bodies produced by Phase 6 synthesis,
    /// and on stdlib programs that bypass per-file <see cref="Run"/>.
    /// Must be called after <see cref="Run"/> has been applied to all user programs.
    /// </summary>
    public void RunGlobal()
    {
        new GlobalEntityRewritePass(ctx: ctx).RunOnVariantBodies();
        new VariantReturnLoweringPass(ctx: ctx).RunOnVariantBodies();
        new LiteralLoweringPass(ctx: ctx).RunOnVariantBodies();
        new BuilderQueryInliningPass(registry: ctx.Registry, variantBodies: ctx.VariantBodies)
           .RunOnVariantBodies();
        new GenericCallLoweringPass(registry: ctx.Registry, variantBodies: ctx.VariantBodies)
           .RunOnVariantBodies();
        // Expand `is Crashable` clauses (synthesized by non-tail propagation in check_/lookup_
        // variants) into per-type TypePatterns before PatternLowering can lower them. No-op for
        // variant bodies that contain no CrashablePattern.
        new CrashableExpansionPass(ctx: ctx).RunOnVariantBodies();
        // PatternLowering runs before ExpressionLowering so that when-clauses with
        // ChainedComparison patterns are converted to IfStatement chains first, allowing
        // ExpressionLowering to correctly lower And/Or in the resulting if-conditions.
        new PatternLoweringPass(ctx: ctx).RunOnVariantBodies();
        var elp = new ExpressionLoweringPass(ctx: ctx);
        elp.RunOnVariantBodies();
        // Second pass to fold NonePattern/None-TypePattern WhenStatements that ExpressionLoweringPass
        // synthesized for `??` / `?.` (or hoisted when-expressions). PLP's lowering may introduce
        // UnaryExpression(Not), so re-run ELP afterwards. Skip the round when no variant body produced
        // a WhenStatement — it would be a pure no-op re-walk over the whole variant-body map.
        if (elp.ProducedWhenStatement)
        {
            new PatternLoweringPass(ctx: ctx).RunOnVariantBodies();
            new ExpressionLoweringPass(ctx: ctx).RunOnVariantBodies();
        }

        new FStringLoweringPass(ctx: ctx).RunOnVariantBodies();
        new OperatorLoweringPass(ctx: ctx).RunOnVariantBodies();
        // See the per-program Run(): rewrite the Roamed raw_inner() projection into a real AST call
        // after operator/f-string lowering so it is visible in synthesized variant bodies too.
        new RoamedProjectionLoweringPass(ctx: ctx).RunOnVariantBodies();
        // See per-program Run(): move the spawn-boundary promote() into a real AST call in
        // synthesized variant bodies too.
        new RoamedSpawnPromotionLoweringPass(ctx: ctx).RunOnVariantBodies();
        new RecordCopyLoweringPass(ctx: ctx).RunOnVariantBodies();
        // NOTE: RcRetainLoweringPass deleted (see per-program Run()): the copy-verb bump lives in the
        // type's own assign/copy derive, and scope-exit teardown handles the decrement.
        // See per-program Run(): move the Roamed field-access lock bracket into real AST calls in
        // synthesized variant bodies too.
        new RoamedLockBracketLoweringPass(ctx: ctx).RunOnVariantBodies();
        new UsingLoweringPass(ctx: ctx).RunOnVariantBodies();
        // CallOverloadResolutionPass runs last so it sees all CallExpression nodes introduced
        // by FStringLoweringPass (represent/diagnose/add), OperatorLoweringPass (wired ops),
        // and RecordCopyLoweringPass (store). Also classifies synthesized derived-operator bodies
        // (ne/lt/le/gt/ge/notcontains) which bypass per-program Run() entirely.
        new CallOverloadResolutionPass(ctx: ctx).RunOnVariantBodies();
        new CallOverloadResolutionPass(ctx: ctx).RunOnSynthesizedBodies();

        // Cold: all stdlib; warm-restore: only freshly on-demand-loaded programs (restored are lowered).
        // STAGE-5 FLIP (pull/(B)): the collector drives per-file stdlib lowering on reach
        // (SemanticVerifier.AnalyzeStdlibProgramOnDemand runs PostprocessingPipeline.Run per file), so skip the
        // eager stdlib sweep here (demand collector drives stdlib per-file).
        // BASE build (SynthesizeAllDerives) stays eager — it must lower the WHOLE stdlib, not a demand slice.
        if (!ctx.SynthesizeAllDerives)
        {
            return;
        }

        foreach ((Program program, _, _) in ctx.Registry.FreshlyLoadedStdlibPrograms)
        {
            Run(program: program);
        }
    }
}
