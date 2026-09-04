using System.Collections.Generic;
using System.Linq;
using TypeModel.Types;
using SyntaxTree;


using Compiler.Postprocessing;
using Compiler.Synthesis;
using Compiler.Instantiation;

namespace Compiler.Postprocessing.Passes;

/// <summary>
/// Expands <see cref="CrashablePattern"/> clauses in <see cref="WhenStatement"/>s whose subject
/// is a <c>Result[T]</c> or <c>Lookup[T]</c> carrier into one <see cref="TypePattern"/> clause
/// per registered crashable type.
///
/// <para>
/// Input:  <c>is Crashable e => body</c> -> matches any error in the carrier.<br/>
/// Output: <c>is ParseError e => body</c>, <c>is NetworkError e => body</c>, ??
///         (one clause per <see cref="CrashableTypeInfo"/> registered in the type registry)
/// </para>
///
/// <para>
/// The expanded <see cref="TypePattern"/> clauses are fully lowerable by the subsequent
/// <see cref="PatternLoweringPass"/>: the condition becomes
/// <c>carrier.type_id == &lt;U64 constant&gt;</c>
/// and the binding becomes a <see cref="CarrierPayloadExpression"/>.
/// </para>
///
/// <para>
/// Conservative expansion: all registered crashable types are emitted, not just the ones
/// thrown by the specific called routine. Extra branches never fire and are eliminated by
/// LLVM dead-code elimination.
/// </para>
///
/// Must run after <see cref="ErrorHandlingVariantPass"/> (global) so that
/// <see cref="TypeModel.Symbols.RoutineInfo.ThrowableTypes"/> is populated,
/// and before <see cref="PatternLoweringPass"/> so the expanded
/// <see cref="TypePattern"/> clauses can be lowered.
/// </summary>
internal sealed class CrashableExpansionPass(PostprocessingContext ctx)
{
    public void Run(Program program)
    {
        List<CrashableTypeInfo> crashableTypes = ctx.Registry
                                                             .GetAllTypes()
                                                             .OfType<CrashableTypeInfo>()
                                                             .ToList();

        // Nothing to expand if no crashable types are registered.
        if (crashableTypes.Count == 0) return;

        for (int i = 0; i < program.Declarations.Count; i++)
        {
            switch (program.Declarations[i])
            {
                case RoutineDeclaration r:
                {
                    Statement newBody = ExpandStatement(stmt: r.Body, crashableTypes: crashableTypes);
                    if (!ReferenceEquals(newBody, r.Body))
                        program.Declarations[i] = r with { Body = newBody };
                    break;
                }

                case EntityDeclaration e:
                    ExpandMemberList(members: e.Members, crashableTypes: crashableTypes);
                    break;

                case RecordDeclaration rec:
                    ExpandMemberList(members: rec.Members, crashableTypes: crashableTypes);
                    break;

                case CrashableDeclaration cr:
                    ExpandMemberList(members: cr.Members, crashableTypes: crashableTypes);
                    break;
            }
        }
    }

    /// <summary>
    /// Expands <see cref="CrashablePattern"/> clauses in error-handling variant bodies
    /// (<see cref="PostprocessingContext.VariantBodies"/>). Needed because non-tail failable-call
    /// propagation in <c>check_</c>/<c>lookup_</c> variants synthesizes
    /// <c>when inner.check_x() { is Crashable e =&gt; ...; else v =&gt; ... }</c>, whose
    /// <c>is Crashable</c> arm must be expanded to per-type <see cref="TypePattern"/>s before
    /// <see cref="PatternLoweringPass.RunOnVariantBodies"/> can lower it.
    /// </summary>
    public void RunOnVariantBodies()
    {
        List<CrashableTypeInfo> crashableTypes = ctx.Registry
                                                             .GetAllTypes()
                                                             .OfType<CrashableTypeInfo>()
                                                             .ToList();
        if (crashableTypes.Count == 0) return;

        foreach (string key in ctx.VariantBodies.Keys.ToList())
        {
            Statement body = ctx.VariantBodies[key];
            Statement expanded = ExpandStatement(stmt: body, crashableTypes: crashableTypes);
            if (!ReferenceEquals(expanded, body))
                ctx.VariantBodies[key] = expanded;
        }
    }

    private void ExpandMemberList(List<SyntaxTree.Declaration> members,
        List<CrashableTypeInfo> crashableTypes)
    {
        for (int j = 0; j < members.Count; j++)
        {
            if (members[j] is not RoutineDeclaration m) continue;
            Statement newBody = ExpandStatement(stmt: m.Body, crashableTypes: crashableTypes);
            if (!ReferenceEquals(newBody, m.Body))
                members[j] = m with { Body = newBody };
        }
    }

    // === Statement walker =========================================================

    private Statement ExpandStatement(Statement stmt, List<CrashableTypeInfo> crashableTypes)
    {
        switch (stmt)
        {
            case WhenStatement w:
                return ExpandWhen(when: w, crashableTypes: crashableTypes);

            case BlockStatement b:
                return ExpandBlock(b: b, crashableTypes: crashableTypes);

            case IfStatement ifs:
                return ExpandIf(ifs: ifs, crashableTypes: crashableTypes);

            case WhileStatement w:
                return ExpandWhile(w: w, crashableTypes: crashableTypes);

            case LoopStatement loop:
            {
                Statement body = ExpandStatement(stmt: loop.Body, crashableTypes: crashableTypes);
                return ReferenceEquals(body, loop.Body) ? loop : loop with { Body = body };
            }

            case EachStatement f:
                return ExpandEach(f: f, crashableTypes: crashableTypes);

            case UsingStatement u:
                return ExpandUsing(u: u, crashableTypes: crashableTypes);

            case DangerStatement d:
            {
                Statement lowered = ExpandStatement(stmt: d.Body, crashableTypes: crashableTypes);
                return !ReferenceEquals(lowered, d.Body)
                    ? d with { Body = (BlockStatement)lowered }
                    : d;
            }

            default:
                return stmt;
        }
    }

    private Statement ExpandBlock(BlockStatement b, List<CrashableTypeInfo> crashableTypes)
    {
        bool changed = false;
        var stmts = new List<Statement>(capacity: b.Statements.Count);
        foreach (Statement s in b.Statements)
        {
            Statement n = ExpandStatement(stmt: s, crashableTypes: crashableTypes);
            stmts.Add(item: n);
            if (!ReferenceEquals(n, s)) changed = true;
        }

        return changed ? b with { Statements = stmts } : b;
    }

    private Statement ExpandIf(IfStatement ifs, List<CrashableTypeInfo> crashableTypes)
    {
        Statement then = ExpandStatement(stmt: ifs.ThenStatement,
            crashableTypes: crashableTypes);
        Statement? elseS = ifs.ElseStatement != null
            ? ExpandStatement(stmt: ifs.ElseStatement, crashableTypes: crashableTypes)
            : null;
        bool changed = !ReferenceEquals(then, ifs.ThenStatement)
                       || !ReferenceEquals(elseS, ifs.ElseStatement);
        return changed
            ? ifs with { ThenStatement = then, ElseStatement = elseS }
            : ifs;
    }

    private Statement ExpandWhile(WhileStatement w, List<CrashableTypeInfo> crashableTypes)
    {
        Statement body = ExpandStatement(stmt: w.Body, crashableTypes: crashableTypes);
        Statement? elseB = w.ElseBranch != null
            ? ExpandStatement(stmt: w.ElseBranch, crashableTypes: crashableTypes)
            : null;
        bool changed = !ReferenceEquals(body, w.Body)
                       || !ReferenceEquals(elseB, w.ElseBranch);
        return changed ? w with { Body = body, ElseBranch = elseB } : w;
    }

    private Statement ExpandEach(EachStatement f, List<CrashableTypeInfo> crashableTypes)
    {
        Statement body = ExpandStatement(stmt: f.Body, crashableTypes: crashableTypes);
        Statement? elseB = f.ElseBranch != null
            ? ExpandStatement(stmt: f.ElseBranch, crashableTypes: crashableTypes)
            : null;
        bool changed = !ReferenceEquals(body, f.Body)
                       || !ReferenceEquals(elseB, f.ElseBranch);
        return changed ? f with { Body = body, ElseBranch = elseB } : f;
    }

    private Statement ExpandUsing(UsingStatement u, List<CrashableTypeInfo> crashableTypes)
    {
        Statement body = ExpandStatement(stmt: u.Body, crashableTypes: crashableTypes);
        Statement? fb = u.FallbackBody != null
            ? ExpandStatement(stmt: u.FallbackBody, crashableTypes: crashableTypes)
            : null;
        return !ReferenceEquals(body, u.Body) || !ReferenceEquals(fb, u.FallbackBody)
            ? u with { Body = body, FallbackBody = fb }
            : u;
    }

    // === WhenStatement expansion ==================================================

    private Statement ExpandWhen(WhenStatement when,
        List<CrashableTypeInfo> crashableTypes)
    {
        // Only expand carrier-type subjects (Result/Lookup).
        // Subject-less when (Expression == null) is never a carrier -> just recurse.
        if (when.Expression == null || !IsResultOrLookup(type: when.Expression.ResolvedType))
        {
            // Still recurse into clause bodies for nested whens.
            return RecurseIntoClauses(when: when, crashableTypes: crashableTypes);
        }

        bool changed = false;
        var expanded = new List<WhenClause>(capacity: when.Clauses.Count);

        foreach (WhenClause clause in when.Clauses)
        {
            if (TryGetCrashableBinding(pattern: clause.Pattern, bindName: out string? bangBindName,
                    loc: out SourceLocation? bangLoc))
            {
                changed = true;
                ExpandCrashableClause(clause: clause, bangBindName: bangBindName, bangLoc: bangLoc!,
                    crashableTypes: crashableTypes, expanded: expanded);
            }
            else
            {
                // Recurse into clause body for nested WhenStatements.
                Statement newBody = ExpandStatement(stmt: clause.Body,
                    crashableTypes: crashableTypes);
                if (!ReferenceEquals(newBody, clause.Body))
                {
                    expanded.Add(item: clause with { Body = newBody });
                    changed = true;
                }
                else
                {
                    expanded.Add(item: clause);
                }
            }
        }

        return changed ? when with { Clauses = expanded } : when;
    }

    /// <summary>
    /// Detects a Crashable-shaped clause pattern, yielding its bound variable name and location.
    /// The parser emits <c>is Crashable err</c> as a <see cref="TypePattern"/> with
    /// <c>Type.Name == "Crashable"</c> (a protocol-style match, not a special pattern node); older
    /// AST paths still produce <see cref="CrashablePattern"/> — both shapes are handled uniformly.
    /// </summary>
    private static bool TryGetCrashableBinding(Pattern pattern, out string? bindName,
        out SourceLocation? loc)
    {
        switch (pattern)
        {
            case CrashablePattern cp:
                bindName = cp.VariableName;
                loc = cp.Location;
                return true;
            case TypePattern { Type.Name: "Crashable" } tp:
                bindName = tp.VariableName;
                loc = tp.Location;
                return true;
            default:
                bindName = null;
                loc = null;
                return false;
        }
    }

    /// <summary>
    /// Replaces one Crashable-shaped clause with N <see cref="TypePattern"/> clauses, one per
    /// registered <see cref="CrashableTypeInfo"/>. Each arm gets its own deep-clone of the body where
    /// the bound name <c>err</c> is rewired to the concrete crashable type, so <c>err.crash_message()</c>
    /// etc. dispatches against a real memberRoutine instead of the bodyless protocol stub.
    /// </summary>
    private void ExpandCrashableClause(WhenClause clause, string? bangBindName, SourceLocation bangLoc,
        List<CrashableTypeInfo> crashableTypes, List<WhenClause> expanded)
    {
        var emptySubs = new Dictionary<string, string>();
        foreach (CrashableTypeInfo crashable in crashableTypes)
        {
            var typeExpr = new TypeExpression(
                Name: crashable.Name,
                GenericArguments: null,
                Location: bangLoc)
            {
                ResolvedType = crashable
            };
            var newPattern = new TypePattern(
                Type: typeExpr,
                VariableName: bangBindName,
                Bindings: null,
                Location: bangLoc);

            Statement clonedBody = GenericAstRewriter.RewriteStatement(
                stmt: clause.Body, subs: emptySubs);
            if (!string.IsNullOrEmpty(value: bangBindName))
            {
                BindingTypeRewriter.Apply(body: clonedBody,
                    bindingName: bangBindName!, concreteType: crashable,
                    registry: ctx.Registry);
            }
            expanded.Add(item: clause with { Pattern = newPattern, Body = clonedBody });
        }
    }

    /// <summary>Recurses into clause bodies without changing the clauses themselves.</summary>
    private WhenStatement RecurseIntoClauses(WhenStatement when,
        List<CrashableTypeInfo> crashableTypes)
    {
        bool changed = false;
        var clauses = new List<WhenClause>(capacity: when.Clauses.Count);
        foreach (WhenClause c in when.Clauses)
        {
            Statement newBody = ExpandStatement(stmt: c.Body, crashableTypes: crashableTypes);
            if (!ReferenceEquals(newBody, c.Body))
            {
                clauses.Add(item: c with { Body = newBody });
                changed = true;
            }
            else
            {
                clauses.Add(item: c);
            }
        }

        return changed ? when with { Clauses = clauses } : when;
    }

    // === Type classification helpers =============================================

    private static bool IsResultOrLookup(TypeInfo? type)
    {
        if (type == null) return false;
        string baseName = GetCarrierBaseName(type: type);
        return baseName is "Result" or "Lookup";
    }

    private static string GetCarrierBaseName(TypeInfo type)
    {
        if (type is RecordTypeInfo { GenericDefinition: not null } r) return r.GenericDefinition.Name;
        if (type is EntityTypeInfo { GenericDefinition: not null } e) return e.GenericDefinition.Name;
        return type.Name;
    }
}
