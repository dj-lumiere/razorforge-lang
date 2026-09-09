using TypeModel.Types;
using SyntaxTree;
using Compiler.Instantiation;

namespace Compiler.Desugaring.Passes;

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
        var crashableTypes = ctx.Registry
                                .GetAllTypes()
                                .OfType<CrashableTypeInfo>()
                                .ToList();

        // Nothing to expand if no crashable types are registered.
        if (crashableTypes.Count == 0)
        {
            return;
        }

        for (int i = 0; i < program.Declarations.Count; i++)
        {
            switch (program.Declarations[index: i])
            {
                case RoutineDeclaration r:
                {
                    Statement newBody =
                        ExpandStatement(stmt: r.Body, crashableTypes: crashableTypes);
                    if (!ReferenceEquals(objA: newBody, objB: r.Body))
                    {
                        program.Declarations[index: i] = r with { Body = newBody };
                    }

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
        var crashableTypes = ctx.Registry
                                .GetAllTypes()
                                .OfType<CrashableTypeInfo>()
                                .ToList();
        if (crashableTypes.Count == 0)
        {
            return;
        }

        foreach (string key in ctx.VariantBodies.Keys.ToList())
        {
            Statement body = ctx.VariantBodies[key: key];
            Statement expanded = ExpandStatement(stmt: body, crashableTypes: crashableTypes);
            if (!ReferenceEquals(objA: expanded, objB: body))
            {
                ctx.VariantBodies[key: key] = expanded;
            }
        }
    }

    private void ExpandMemberList(List<SyntaxTree.Declaration> members,
        List<CrashableTypeInfo> crashableTypes)
    {
        for (int j = 0; j < members.Count; j++)
        {
            if (members[index: j] is not RoutineDeclaration m)
            {
                continue;
            }

            Statement newBody = ExpandStatement(stmt: m.Body, crashableTypes: crashableTypes);
            if (!ReferenceEquals(objA: newBody, objB: m.Body))
            {
                members[index: j] = m with { Body = newBody };
            }
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
                return ReferenceEquals(objA: body, objB: loop.Body)
                    ? loop
                    : loop with { Body = body };
            }

            case EachStatement f:
                return ExpandEach(f: f, crashableTypes: crashableTypes);

            case UsingStatement u:
                return ExpandUsing(u: u, crashableTypes: crashableTypes);

            case DangerStatement d:
            {
                Statement lowered = ExpandStatement(stmt: d.Body, crashableTypes: crashableTypes);
                return !ReferenceEquals(objA: lowered, objB: d.Body)
                    ? d with { Body = (BlockStatement)lowered }
                    : d;
            }

            default:
                return stmt;
        }
    }

    private BlockStatement ExpandBlock(BlockStatement b, List<CrashableTypeInfo> crashableTypes)
    {
        bool changed = false;
        var stmts = new List<Statement>(capacity: b.Statements.Count);
        foreach (Statement s in b.Statements)
        {
            Statement n = ExpandStatement(stmt: s, crashableTypes: crashableTypes);
            stmts.Add(item: n);
            if (!ReferenceEquals(objA: n, objB: s))
            {
                changed = true;
            }
        }

        return changed
            ? b with { Statements = stmts }
            : b;
    }

    private IfStatement ExpandIf(IfStatement ifs, List<CrashableTypeInfo> crashableTypes)
    {
        Statement then = ExpandStatement(stmt: ifs.ThenStatement, crashableTypes: crashableTypes);
        Statement? elseS = ifs.ElseStatement != null
            ? ExpandStatement(stmt: ifs.ElseStatement, crashableTypes: crashableTypes)
            : null;
        bool changed = !ReferenceEquals(objA: then, objB: ifs.ThenStatement) ||
                       !ReferenceEquals(objA: elseS, objB: ifs.ElseStatement);
        return changed
            ? ifs with { ThenStatement = then, ElseStatement = elseS }
            : ifs;
    }

    private WhileStatement ExpandWhile(WhileStatement w, List<CrashableTypeInfo> crashableTypes)
    {
        Statement body = ExpandStatement(stmt: w.Body, crashableTypes: crashableTypes);
        Statement? elseB = w.ElseBranch != null
            ? ExpandStatement(stmt: w.ElseBranch, crashableTypes: crashableTypes)
            : null;
        bool changed = !ReferenceEquals(objA: body, objB: w.Body) ||
                       !ReferenceEquals(objA: elseB, objB: w.ElseBranch);
        return changed
            ? w with { Body = body, ElseBranch = elseB }
            : w;
    }

    private EachStatement ExpandEach(EachStatement f, List<CrashableTypeInfo> crashableTypes)
    {
        Statement body = ExpandStatement(stmt: f.Body, crashableTypes: crashableTypes);
        Statement? elseB = f.ElseBranch != null
            ? ExpandStatement(stmt: f.ElseBranch, crashableTypes: crashableTypes)
            : null;
        bool changed = !ReferenceEquals(objA: body, objB: f.Body) ||
                       !ReferenceEquals(objA: elseB, objB: f.ElseBranch);
        return changed
            ? f with { Body = body, ElseBranch = elseB }
            : f;
    }

    private UsingStatement ExpandUsing(UsingStatement u, List<CrashableTypeInfo> crashableTypes)
    {
        Statement body = ExpandStatement(stmt: u.Body, crashableTypes: crashableTypes);
        Statement? fb = u.FallbackBody != null
            ? ExpandStatement(stmt: u.FallbackBody, crashableTypes: crashableTypes)
            : null;
        return !ReferenceEquals(objA: body, objB: u.Body) ||
               !ReferenceEquals(objA: fb, objB: u.FallbackBody)
            ? u with { Body = body, FallbackBody = fb }
            : u;
    }

    // === WhenStatement expansion ==================================================

    private WhenStatement ExpandWhen(WhenStatement when, List<CrashableTypeInfo> crashableTypes)
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
            if (TryGetCrashableBinding(pattern: clause.Pattern,
                    bindName: out string? bangBindName,
                    loc: out SourceLocation? bangLoc))
            {
                changed = true;
                // Prefer the runtime-dispatch rewrite (freeze-safe: the carrier body stays independent of
                // the crashable set, so the stdlib snapshot can freeze it and a warm compile still picks up
                // user crashables). Only valid when the binding is used SOLELY via the dispatchable Crashable
                // members (represent/diagnose/crash_message/crash_title). Otherwise — variant propagation
                // (`throw e`/`return e`), or a binding still inside an un-lowered f-string — fall back to the
                // per-type fan-out, which binds the concrete crashable.
                WhenClause? dispatchClause = TryMakeCrashableDispatchClause(clause: clause,
                    bindName: bangBindName,
                    loc: bangLoc!,
                    carrier: when.Expression!);
                if (dispatchClause != null)
                {
                    expanded.Add(item: dispatchClause);
                }
                else
                {
                    ExpandCrashableClause(clause: clause,
                        bangBindName: bangBindName,
                        bangLoc: bangLoc!,
                        crashableTypes: crashableTypes,
                        expanded: expanded);
                }
            }
            else
            {
                // Recurse into clause body for nested WhenStatements.
                Statement newBody = ExpandStatement(stmt: clause.Body,
                    crashableTypes: crashableTypes);
                if (!ReferenceEquals(objA: newBody, objB: clause.Body))
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

        return changed
            ? when with { Clauses = expanded }
            : when;
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
    /// The zero-arg Crashable protocol members that can be dispatched at runtime off a type-erased error
    /// (all <c>-&gt; Text</c>). A binding used ONLY via these can take the runtime-dispatch path; any other
    /// use (as a value) forces the per-type fan-out.
    /// </summary>
    private static readonly HashSet<string> DispatchableCrashableMembers =
        new(comparer: StringComparer.Ordinal)
        {
            Declaration.RuntimeContract.Display.Represent,
            Declaration.RuntimeContract.Display.Diagnose,
            Declaration.RuntimeContract.CrashMessage,
            Declaration.RuntimeContract.CrashTitle
        };

    /// <summary>
    /// Builds a single freeze-safe <see cref="CrashablePattern"/> clause whose body has every
    /// <c>&lt;bind&gt;.&lt;member&gt;()</c> call rewritten to a <see cref="CrashableDispatchExpression"/>
    /// reading the erased error off <paramref name="carrier"/>. Returns null (caller falls back to the
    /// per-type fan-out) when the binding is used any OTHER way — a leftover reference after the rewrite
    /// means the binding escaped as a value (variant propagation, un-lowered f-string, etc.).
    /// </summary>
    private static WhenClause? TryMakeCrashableDispatchClause(WhenClause clause, string? bindName,
        SourceLocation loc, Expression carrier)
    {
        Statement body = clause.Body;
        if (!string.IsNullOrEmpty(value: bindName))
        {
            body = new CrashableDispatchRewriter(bindName: bindName!, carrier: carrier)
               .VisitStatement(stmt: body);
            if (BindingStillReferenced(root: body, bindName: bindName!))
            {
                return null;
            }
        }

        return clause with
        {
            Pattern = new CrashablePattern(ErrorType: null, VariableName: null, Location: loc),
            Body = body
        };
    }

    /// <summary>True when any <see cref="IdentifierExpression"/> named <paramref name="bindName"/> remains
    /// in <paramref name="root"/> after the dispatch rewrite — i.e. the binding is used as a value.</summary>
    private static bool BindingStillReferenced(Statement root, string bindName)
    {
        bool found = false;
        AstWalker.WalkExpressions(root: root,
            visit: e =>
            {
                if (e is IdentifierExpression id && id.Name == bindName)
                {
                    found = true;
                }
            });
        return found;
    }

    /// <summary>
    /// Rewrites <c>&lt;bind&gt;.&lt;dispatchable member&gt;()</c> calls in a Crashable arm body to a
    /// <see cref="CrashableDispatchExpression"/> over the carrier. Non-dispatchable uses of the binding are
    /// left intact (and detected afterward by <see cref="BindingStillReferenced"/> to force the fan-out).
    /// </summary>
    private sealed class CrashableDispatchRewriter(string bindName, Expression carrier)
        : AstRewriter
    {
        protected override Expression VisitCall(CallExpression e)
        {
            if (e is
                {
                    Arguments.Count: 0,
                    Callee: MemberExpression { Object: IdentifierExpression id } m
                } && id.Name == bindName &&
                DispatchableCrashableMembers.Contains(item: m.MemberName))
            {
                return new CrashableDispatchExpression(Carrier: carrier,
                    MemberName: m.MemberName,
                    Location: e.Location) { ResolvedType = e.ResolvedType };
            }

            return base.VisitCall(e: e);
        }
    }

    /// <summary>
    /// Replaces one Crashable-shaped clause with N <see cref="TypePattern"/> clauses, one per
    /// registered <see cref="CrashableTypeInfo"/>. Each arm gets its own deep-clone of the body where
    /// the bound name <c>err</c> is rewired to the concrete crashable type, so <c>err.crash_message()</c>
    /// etc. dispatches against a real memberRoutine instead of the bodyless protocol stub.
    /// </summary>
    private void ExpandCrashableClause(WhenClause clause, string? bangBindName,
        SourceLocation bangLoc, List<CrashableTypeInfo> crashableTypes, List<WhenClause> expanded)
    {
        var emptySubs = new Dictionary<string, string>();
        foreach (CrashableTypeInfo crashable in crashableTypes)
        {
            var typeExpr = new TypeExpression(
                Name: crashable.Name,
                GenericArguments: null,
                Location: bangLoc) { ResolvedType = crashable };
            var newPattern = new TypePattern(Type: typeExpr,
                VariableName: bangBindName,
                Bindings: null,
                Location: bangLoc);

            Statement clonedBody = GenericAstRewriter.RewriteStatement(
                stmt: clause.Body,
                subs: emptySubs);
            if (!string.IsNullOrEmpty(value: bangBindName))
            {
                BindingTypeRewriter.Apply(body: clonedBody,
                    bindingName: bangBindName!,
                    concreteType: crashable,
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
            if (!ReferenceEquals(objA: newBody, objB: c.Body))
            {
                clauses.Add(item: c with { Body = newBody });
                changed = true;
            }
            else
            {
                clauses.Add(item: c);
            }
        }

        return changed
            ? when with { Clauses = clauses }
            : when;
    }

    // === Type classification helpers =============================================

    private static bool IsResultOrLookup(TypeInfo? type)
    {
        if (type == null)
        {
            return false;
        }

        string baseName = GetCarrierBaseName(type: type);
        return baseName is "Result" or "Lookup";
    }

    private static string GetCarrierBaseName(TypeInfo type)
    {
        if (type is RecordTypeInfo { GenericDefinition: not null } r)
        {
            return r.GenericDefinition.Name;
        }

        if (type is EntityTypeInfo { GenericDefinition: not null } e)
        {
            return e.GenericDefinition.Name;
        }

        return type.Name;
    }
}
