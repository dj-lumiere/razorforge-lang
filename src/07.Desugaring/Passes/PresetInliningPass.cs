using System.Collections.Generic;
using System.Linq;
using Compiler.Resolution;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Compiler.Desugaring.Passes;

/// <summary>
/// Substitutes <c>IdentifierExpression</c> nodes for preset constants with the
/// preset's literal value expression.
///
/// <para>Must run <em>first</em> in the per-file desugaring pipeline so that all
/// subsequent passes (operator lowering, f-string lowering, etc.) operate on
/// concrete literal values rather than named identifiers. For example,</para>
/// <code>
/// preset RUNS: S64 = 20_s64
/// for _ in 0..RUNS ?? for _ in 0..S64(20)
/// </code>
/// <para>Needs <see cref="VariableInfo.PresetValue"/> to be set on preset entries in
/// the <see cref="TypeRegistry"/>, which happens during
/// <c>CollectPresetDeclaration</c> in Phase 3.</para>
/// </summary>
internal sealed class PresetInliningPass(DesugaringContext ctx) : AstRewriter
{
    // Presets are GLOBAL by default (public constants like `S64_MAX`/`DECIMAL_PI` are part of the Core
    // prelude and usable everywhere). A `secret preset` is FILE-PRIVATE: inlinable only inside the file
    // that declares it. `_ownPresets` is the set of preset names declared in the file currently
    // being lowered — a secret preset from another file is NOT in it, so it is left un-inlined and a bare
    // identifier that merely shares a name (e.g. a user `record B` vs a stdlib `secret preset B`) resolves
    // as its own declaration. Null means "no scoping" (compiler-synthesized variant bodies).
    //
    // NOTE (module-private secret, WIP): the intended end-state is that `secret` is MODULE-private for
    // ACCESS (un-importable, usable across files of the same module) enforced at the VISIBILITY layer with
    // a diagnostic — NOT by widening inlining to module scope here. A prior attempt to inline within-module
    // secret presets regressed reachability (Dict/Set memberRoutine bodies pruned → 48 "never defined" in the
    // stdlib harness). Keep inlining FILE-scoped; enforce module-private access separately.
    private Dictionary<string, PresetDeclaration>? _ownPresets;

    /// <summary>Every <c>preset</c> declared in the current file, keyed by name (public or secret).</summary>
    private static Dictionary<string, PresetDeclaration> CollectOwnPresets(Program program)
    {
        var map = new Dictionary<string, PresetDeclaration>(comparer: StringComparer.Ordinal);
        foreach (ISyntaxTreeNode decl in program.Declarations)
            if (decl is PresetDeclaration preset)
                map[key: preset.Name] = preset;
        return map;
    }

    /// <summary>
    /// Runs this compiler phase over its configured input.
    /// </summary>
    public void Run(Program program)
    {
        _ownPresets = CollectOwnPresets(program: program);
        for (int i = 0; i < program.Declarations.Count; i++)
        {
            switch (program.Declarations[i])
            {
                case RoutineDeclaration r:
                {
                    Statement newBody = VisitStatement(r.Body);
                    if (!ReferenceEquals(newBody, r.Body))
                        program.Declarations[i] = r with { Body = newBody };
                    break;
                }

                case EntityDeclaration e:
                    LowerMemberList(e.Members);
                    break;

                case RecordDeclaration rec:
                    LowerMemberList(rec.Members);
                    break;

                case CrashableDeclaration cr:
                    LowerMemberList(cr.Members);
                    break;
            }
        }
    }

    /// <summary>
    /// Inlines presets in all synthesized bodies in <see cref="DesugaringContext.VariantBodies"/>.
    /// Called from <see cref="DesugaringPipeline.RunGlobal"/> after variant bodies are generated.
    /// </summary>
    public void RunOnVariantBodies()
    {
        // Synthesized variant bodies are compiler-generated and not tied to a source file. They never
        // reference file-private secret presets, so an empty own-set is correct (public presets still
        // inline via the registry).
        _ownPresets = new Dictionary<string, PresetDeclaration>(comparer: StringComparer.Ordinal);
        foreach (string key in ctx.VariantBodies.Keys.ToList())
        {
            if (ctx.RestoredVariantKeys.Contains(item: key)) continue; // already inlined at snapshot capture
            Statement body = ctx.VariantBodies[key];
            Statement lowered = VisitStatement(body);
            if (!ReferenceEquals(lowered, body))
                ctx.VariantBodies[key] = lowered;
        }
    }

    /// <summary>
    /// Lower member list as part of this compiler phase.
    /// </summary>
    private void LowerMemberList(List<SyntaxTree.Declaration> members)
    {
        for (int j = 0; j < members.Count; j++)
        {
            if (members[j] is not RoutineDeclaration m) continue;
            Statement newBody = VisitStatement(m.Body);
            if (!ReferenceEquals(newBody, m.Body))
                members[j] = m with { Body = newBody };
        }
    }

    // -----------------------------------------------------------------------------

    /// <summary>
    /// Inlines an identifier that resolves to a preset constant with its literal value, honoring
    /// secret-preset file scoping and aggregate-preset exclusion. Returns the identifier unchanged
    /// when it is not an inlinable preset.
    /// </summary>
    private Expression LowerIdentifier(Expression expr, IdentifierExpression id)
    {
        VariableInfo? v = ctx.Registry.LookupVariable(id.Name);

        // A `secret preset` is MODULE-private: inline it inside any file of the module that declares
        // it (same granularity as `secret record`/`secret entity`). A reference from ANOTHER module is
        // left un-inlined — since it stays a bare identifier that resolves to a preset, the
        // backend-entry validator flags it (RF-S958), so a secret constant cannot silently be used
        // from another module. Public presets always inline. When there is no current module
        // (`_currentModule == null` for synthesized variant bodies), a secret with a known module is
        // treated as foreign and left un-inlined, preserving the prior variant-body behavior.
        if (v is { IsPreset: true, IsSecret: true }
            && _ownPresets is not null
            && !_ownPresets.ContainsKey(key: id.Name))
        {
            return expr;
        }

        if (v is { IsPreset: true, PresetValue: not null })
        {
            // Aggregate (Array[T,N]) presets are NOT inlined: substituting the whole list
            // literal at every use site rebuilds the array per reference (codegen lowered it to
            // a heap List rebuilt element-by-element — the fun_bench OOM). Keep the identifier so
            // codegen emits a single `@preset.*` constant global and indexes into it.
            if (v.IsPresettableAggregate)
                return expr;

            // Carry the Phase-4 ResolvedType from the identifier onto the inlined value.
            // This ensures operator-lowering and other subsequent passes see the correct type.
            TypeInfo? resolvedType = id.ResolvedType ?? v.PresetValue.ResolvedType;
            return v.PresetValue is LiteralExpression lit
                ? lit with { ResolvedType = resolvedType }
                : v.PresetValue;
        }
        return expr;
    }

    /// <summary>
    /// Two node kinds this pass rewrites are NOT part of the shared <see cref="AstRewriter"/> spine, so
    /// they are dispatched here on top of the base:
    /// <list type="bullet">
    ///   <item><see cref="IdentifierExpression"/> — a leaf to the base, but the whole POINT of this pass:
    ///   inline a preset identifier to its literal value (via <see cref="LowerIdentifier"/>).</item>
    ///   <item><see cref="WaitforExpression"/> — the base treats it as a leaf (no <c>VisitWaitfor</c> hook),
    ///   but a preset may appear in its operand/timeout, so recurse into those parts explicitly.</item>
    /// </list>
    /// Every other node kind is left to the base's structural recursion.
    /// </summary>
    public override Expression VisitExpression(Expression expr)
    {
        if (expr is IdentifierExpression id)
            return LowerIdentifier(expr: expr, id: id);

        if (expr is WaitforExpression wf)
        {
            Expression o = VisitExpression(wf.Operand);
            Expression? timeout = wf.Timeout != null ? VisitExpression(wf.Timeout) : null;
            bool changed = !ReferenceEquals(o, wf.Operand)
                           || !ReferenceEquals(timeout, wf.Timeout);
            return changed ? wf with { Operand = o, Timeout = timeout } : expr;
        }

        return base.VisitExpression(expr);
    }

    /// <summary>
    /// A bare-identifier callee is a TYPE constructor or a routine name — never a preset value (presets
    /// are scalars/aggregates, not callable). Inlining it would rewrite <c>Foo(a: 1)</c> into
    /// <c>&lt;literal&gt;(a: 1)</c> when a preset happens to share the name <c>Foo</c> (e.g. a user
    /// <c>record B</c> colliding with stdlib <c>preset B</c>). So skip the callee when it is a bare
    /// identifier; still lower a member/other callee (e.g. <c>SOME_PRESET.bit_count()</c> whose Object
    /// may be a preset) and the argument list.
    /// </summary>
    protected override Expression VisitCall(CallExpression e)
    {
        Expression callee = e.Callee is IdentifierExpression
            ? e.Callee
            : VisitExpression(e.Callee);
        List<Expression> args = RewriteList(e.Arguments, VisitExpression);
        bool changed = !ReferenceEquals(callee, e.Callee)
                       || !ReferenceEquals(args, e.Arguments);
        return changed ? e with { Callee = callee, Arguments = args } : e;
    }
}
