using Compiler.Tokenizer;
using SyntaxTree;
using TypeModel.Types;
using TypeInfo = TypeModel.Types.TypeInfo;

namespace Compiler.Verification;

public partial class SemanticVerifier
{
    /// <summary>
    /// Folds constant list-returning BuilderQuery reflection calls
    /// (<see cref="BuilderInfoProvider.ListReturningConstantRoutines"/>: <c>routine_names</c>/<c>protocols</c>/
    /// <c>generic_args</c>/<c>annotations</c>/<c>dependencies</c>) into inline analyzed <c>List[Text]</c>
    /// literals — the list analogue of the scalar BuilderQuery fold. These routines are NOT synthesized as
    /// bodies (<c>WiredRoutinePass.TryHandleBuilderQueryConstant</c> returns early); the value is a
    /// compile-time constant of the concrete receiver type, recomputed here and emitted as a source-shaped
    /// <c>[...]</c> literal (so it carries a monomorphized <c>from_literal(Array[Text,N])</c> builder that
    /// reachability seeds like any other collection literal).
    ///
    /// <para>Runs BEFORE reachability (start of <c>RunPhase7Instantiation</c>): folding here means RRP walks
    /// the inline literal (not a call to a routine), so no dead reflection routine survives — the invariant
    /// "BuilderQuery does not survive desugaring as an emitted routine" holds. The non-pruned resident-JIT
    /// base then no longer emits per-type reflection routines dangling an unmaterialized from_literal.
    /// </para>
    /// </summary>
    private void FoldListBuilderQueryReflection()
    {
        FoldReflectionInPrograms(programs: _registry.UserPrograms);
        FoldReflectionInPrograms(programs: _registry.FreshlyLoadedStdlibPrograms);
        foreach (string key in _variantBodies.Keys.ToList())
        {
            Statement lowered = FoldReflectionStmt(stmt: _variantBodies[key]);
            if (!ReferenceEquals(lowered, _variantBodies[key]))
                _variantBodies[key] = lowered;
        }
    }

    private void FoldReflectionInPrograms(
        List<(Program Program, string FilePath, string Module)> programs)
    {
        foreach ((Program program, _, _) in programs)
        {
            for (int i = 0; i < program.Declarations.Count; i++)
            {
                switch (program.Declarations[i])
                {
                    case RoutineDeclaration r:
                    {
                        Statement nb = FoldReflectionStmt(stmt: r.Body);
                        if (!ReferenceEquals(nb, r.Body)) program.Declarations[i] = r with { Body = nb };
                        break;
                    }
                    case EntityDeclaration e: FoldReflectionMembers(members: e.Members); break;
                    case RecordDeclaration rec: FoldReflectionMembers(members: rec.Members); break;
                    case CrashableDeclaration cr: FoldReflectionMembers(members: cr.Members); break;
                }
            }
        }
    }

    private void FoldReflectionMembers(List<SyntaxTree.Declaration> members)
    {
        for (int j = 0; j < members.Count; j++)
        {
            if (members[j] is not RoutineDeclaration m) continue;
            Statement nb = FoldReflectionStmt(stmt: m.Body);
            if (!ReferenceEquals(nb, m.Body)) members[j] = m with { Body = nb };
        }
    }

    private Statement FoldReflectionStmt(Statement stmt)
    {
        switch (stmt)
        {
            case BlockStatement b: return FoldBlockStmt(stmt: stmt, b: b);
            case IfStatement ifs: return FoldIfStmt(stmt: stmt, ifs: ifs);
            case WhileStatement w: return FoldWhileStmt(stmt: stmt, w: w);
            case LoopStatement loop: return FoldLoopStmt(stmt: stmt, loop: loop);
            case EachStatement f: return FoldEachStmt(stmt: stmt, f: f);
            case WhenStatement ws: return FoldWhenStmt(stmt: stmt, ws: ws);
            case ReturnStatement { Value: not null } ret: return FoldReturnStmt(stmt: stmt, ret: ret);
            case AssignmentStatement asg: return FoldAssignmentStmt(stmt: stmt, asg: asg);
            case DeclarationStatement { Declaration: VariableDeclaration { Initializer: not null } vd } ds:
                return FoldDeclarationStmt(stmt: stmt, ds: ds, vd: vd);
            case ExpressionStatement es: return FoldExpressionStmt(stmt: stmt, es: es);
            case DiscardStatement dsc: return FoldDiscardStmt(stmt: stmt, dsc: dsc);
            case ThrowStatement ts: return FoldThrowStmt(stmt: stmt, ts: ts);
            case BecomesStatement bs: return FoldBecomesStmt(stmt: stmt, bs: bs);
            case UsingStatement us: return FoldUsingStmt(stmt: stmt, us: us);
            case DangerStatement dg: return FoldDangerStmt(stmt: stmt, dg: dg);
            default:
                return stmt;
        }
    }

    private Statement FoldBlockStmt(Statement stmt, BlockStatement b)
    {
        bool changed = false;
        var list = new List<Statement>(capacity: b.Statements.Count);
        foreach (Statement s in b.Statements)
        {
            Statement ns = FoldReflectionStmt(stmt: s);
            list.Add(item: ns);
            if (!ReferenceEquals(ns, s)) changed = true;
        }
        return changed ? b with { Statements = list } : stmt;
    }

    private Statement FoldIfStmt(Statement stmt, IfStatement ifs)
    {
        Expression c = FoldReflectionExpr(expr: ifs.Condition);
        Statement t = FoldReflectionStmt(stmt: ifs.ThenStatement);
        Statement? e = ifs.ElseStatement != null ? FoldReflectionStmt(stmt: ifs.ElseStatement) : null;
        return !ReferenceEquals(c, ifs.Condition) || !ReferenceEquals(t, ifs.ThenStatement)
               || !ReferenceEquals(e, ifs.ElseStatement)
            ? ifs with { Condition = c, ThenStatement = t, ElseStatement = e } : stmt;
    }

    private Statement FoldWhileStmt(Statement stmt, WhileStatement w)
    {
        Expression c = FoldReflectionExpr(expr: w.Condition);
        Statement body = FoldReflectionStmt(stmt: w.Body);
        return !ReferenceEquals(c, w.Condition) || !ReferenceEquals(body, w.Body)
            ? w with { Condition = c, Body = body } : stmt;
    }

    private Statement FoldLoopStmt(Statement stmt, LoopStatement loop)
    {
        Statement body = FoldReflectionStmt(stmt: loop.Body);
        return ReferenceEquals(body, loop.Body) ? stmt : loop with { Body = body };
    }

    private Statement FoldEachStmt(Statement stmt, EachStatement f)
    {
        Expression it = FoldReflectionExpr(expr: f.Iterable);
        Statement body = FoldReflectionStmt(stmt: f.Body);
        return !ReferenceEquals(it, f.Iterable) || !ReferenceEquals(body, f.Body)
            ? f with { Iterable = it, Body = body } : stmt;
    }

    private Statement FoldWhenStmt(Statement stmt, WhenStatement ws)
    {
        Expression subj = FoldReflectionExpr(expr: ws.Expression);
        bool changed = !ReferenceEquals(subj, ws.Expression);
        var clauses = new List<WhenClause>(capacity: ws.Clauses.Count);
        foreach (WhenClause cl in ws.Clauses)
        {
            Statement cb = FoldReflectionStmt(stmt: cl.Body);
            if (!ReferenceEquals(cb, cl.Body)) changed = true;
            clauses.Add(item: !ReferenceEquals(cb, cl.Body) ? cl with { Body = cb } : cl);
        }
        return changed ? ws with { Expression = subj, Clauses = clauses } : stmt;
    }

    private Statement FoldReturnStmt(Statement stmt, ReturnStatement ret)
    {
        Expression retValue = ret.Value!;
        Expression v = FoldReflectionExpr(expr: retValue);
        return ReferenceEquals(v, retValue) ? stmt : ret with { Value = v };
    }

    private Statement FoldAssignmentStmt(Statement stmt, AssignmentStatement asg)
    {
        Expression v = FoldReflectionExpr(expr: asg.Value);
        return ReferenceEquals(v, asg.Value) ? stmt : asg with { Value = v };
    }

    private Statement FoldDeclarationStmt(Statement stmt, DeclarationStatement ds, VariableDeclaration vd)
    {
        Expression vdInitializer = vd.Initializer!;
        Expression init = FoldReflectionExpr(expr: vdInitializer);
        return ReferenceEquals(init, vdInitializer) ? stmt
            : ds with { Declaration = vd with { Initializer = init } };
    }

    private Statement FoldExpressionStmt(Statement stmt, ExpressionStatement es)
    {
        Expression e = FoldReflectionExpr(expr: es.Expression);
        return ReferenceEquals(e, es.Expression) ? stmt : es with { Expression = e };
    }

    private Statement FoldDiscardStmt(Statement stmt, DiscardStatement dsc)
    {
        Expression e = FoldReflectionExpr(expr: dsc.Expression);
        return ReferenceEquals(e, dsc.Expression) ? stmt : dsc with { Expression = e };
    }

    private Statement FoldThrowStmt(Statement stmt, ThrowStatement ts)
    {
        Expression e = FoldReflectionExpr(expr: ts.Error);
        return ReferenceEquals(e, ts.Error) ? stmt : ts with { Error = e };
    }

    private Statement FoldBecomesStmt(Statement stmt, BecomesStatement bs)
    {
        Expression v = FoldReflectionExpr(expr: bs.Value);
        return ReferenceEquals(v, bs.Value) ? stmt : bs with { Value = v };
    }

    private Statement FoldUsingStmt(Statement stmt, UsingStatement us)
    {
        Statement body = FoldReflectionStmt(stmt: us.Body);
        Statement? fb = us.FallbackBody != null ? FoldReflectionStmt(stmt: us.FallbackBody) : null;
        return ReferenceEquals(body, us.Body) && ReferenceEquals(fb, us.FallbackBody)
            ? stmt : us with { Body = body, FallbackBody = fb };
    }

    private Statement FoldDangerStmt(Statement stmt, DangerStatement dg)
    {
        Statement nb = FoldReflectionStmt(stmt: dg.Body);
        return !ReferenceEquals(nb, dg.Body) && nb is BlockStatement bs2 ? dg with { Body = bs2 } : stmt;
    }

    private Expression FoldReflectionExpr(Expression expr)
    {
        // Leaf fold: a 0-arg member call to a constant list-returning BuilderQuery reflection routine on a
        // concrete receiver → inline analyzed List[Text] literal.
        if (expr is CallExpression
            {
                Callee: MemberExpression { MemberName: var rn, Object: { } recv } , Arguments: { Count: 0 }
            } bqCall
            && BuilderInfoProvider.IsListReturningConstantRoutine(name: rn)
            && recv.ResolvedType is { } owner
            && owner is not GenericParameterTypeInfo
            && !owner.IsGenericDefinition)
        {
            ListLiteralExpression? folded = FoldReflectionCall(owner: owner, routineName: rn,
                returnType: bqCall.ResolvedRoutine?.ReturnType, loc: bqCall.Location);
            if (folded != null) return folded;
        }

        Expression result = FoldReflectionExprStructural(expr: expr);
        // Record with-expressions copy only primary-constructor properties, not the mutable ResolvedType
        // that semantic analysis annotated. A structural rebuild therefore drops ResolvedType, and the
        // expression-lowering pass later throws "reached without a resolved type". The rebuilt node
        // carries the same logical expression and type, so restore it here. Cases that already set a
        // specific type (folded call or index nodes) leave ResolvedType non-null and are untouched.
        if (!ReferenceEquals(objA: result, objB: expr) && result.ResolvedType is null)
            result.ResolvedType = expr.ResolvedType;
        return result;
    }

    private Expression FoldReflectionExprStructural(Expression expr)
    {
        switch (expr)
        {
            case BinaryExpression bin: return FoldBinaryExpr(expr: expr, bin: bin);
            case UnaryExpression un: return FoldUnaryExpr(expr: expr, un: un);
            case CallExpression call: return FoldCallExpr(expr: expr, call: call);
            case NamedArgumentExpression na: return FoldNamedArgumentExpr(expr: expr, na: na);
            case MemberExpression mem: return FoldMemberExpr(expr: expr, mem: mem);
            case OptionalMemberExpression om: return FoldOptionalMemberExpr(expr: expr, om: om);
            case IndexExpression ix: return FoldIndexExpr(expr: expr, ix: ix);
            case TypeConversionExpression cv: return FoldTypeConversionExpr(expr: expr, cv: cv);
            case StealExpression st: return FoldStealExpr(expr: expr, st: st);
            case GenericMemberRoutineCallExpression gmc: return FoldGenericMemberRoutineCallExpr(expr: expr, gmc: gmc);
            case GenericMemberExpression gm: return FoldGenericMemberExpr(expr: expr, gm: gm);
            case IsPatternExpression ip: return FoldIsPatternExpr(expr: expr, ip: ip);
            case FlagsTestExpression ft: return FoldFlagsTestExpr(expr: expr, ft: ft);
            case ChainedComparisonExpression ch: return FoldChainedComparisonExpr(expr: expr, ch: ch);
            case CompoundAssignmentExpression cp: return FoldCompoundAssignmentExpr(expr: expr, cp: cp);
            case RangeExpression rg: return FoldRangeExpr(expr: expr, rg: rg);
            case ConditionalExpression co: return FoldConditionalExpr(expr: expr, co: co);
            case TupleLiteralExpression tp: return FoldTupleLiteralExpr(expr: expr, tp: tp);
            case ListLiteralExpression ll: return FoldListLiteralExpr(expr: expr, ll: ll);
            case SetLiteralExpression se: return FoldSetLiteralExpr(expr: expr, se: se);
            case DictLiteralExpression di: return FoldDictLiteralExpr(expr: expr, di: di);
            case CreatorExpression cr: return FoldCreatorExpr(expr: expr, cr: cr);
            case InsertedTextExpression fs: return FoldInsertedTextExpr(expr: expr, fs: fs);
            case BlockExpression bl: return FoldBlockExpr(expr: expr, bl: bl);
            case CarrierPayloadExpression cpe: return FoldCarrierPayloadExpr(expr: expr, cpe: cpe);
            default:
                return expr;
        }
    }

    private Expression FoldBinaryExpr(Expression expr, BinaryExpression bin)
    {
        Expression l = FoldReflectionExpr(expr: bin.Left);
        Expression r = FoldReflectionExpr(expr: bin.Right);
        return ReferenceEquals(l, bin.Left) && ReferenceEquals(r, bin.Right)
            ? expr : bin with { Left = l, Right = r };
    }

    private Expression FoldUnaryExpr(Expression expr, UnaryExpression un)
    {
        Expression o = FoldReflectionExpr(expr: un.Operand);
        return ReferenceEquals(o, un.Operand) ? expr : un with { Operand = o };
    }

    private Expression FoldCallExpr(Expression expr, CallExpression call)
    {
        Expression callee = FoldReflectionExpr(expr: call.Callee);
        List<Expression> args = FoldReflectionList(list: call.Arguments);
        if (ReferenceEquals(callee, call.Callee) && ReferenceEquals(args, call.Arguments)) return expr;
        var rw = call with { Callee = callee, Arguments = args };
        rw.ResolvedRoutine = call.ResolvedRoutine;
        rw.LoweringKind = call.LoweringKind;
        rw.ConstructedType = call.ConstructedType;
        rw.IsCollectionLiteral = call.IsCollectionLiteral;
        rw.TypeArguments = call.TypeArguments;
        rw.ResolvedType = call.ResolvedType;
        return rw;
    }

    private Expression FoldNamedArgumentExpr(Expression expr, NamedArgumentExpression na)
    {
        Expression v = FoldReflectionExpr(expr: na.Value);
        return ReferenceEquals(v, na.Value) ? expr : na with { Value = v };
    }

    private Expression FoldMemberExpr(Expression expr, MemberExpression mem)
    {
        Expression o = FoldReflectionExpr(expr: mem.Object);
        return ReferenceEquals(o, mem.Object) ? expr : mem with { Object = o };
    }

    private Expression FoldOptionalMemberExpr(Expression expr, OptionalMemberExpression om)
    {
        Expression o = FoldReflectionExpr(expr: om.Object);
        return ReferenceEquals(o, om.Object) ? expr : om with { Object = o };
    }

    private Expression FoldIndexExpr(Expression expr, IndexExpression ix)
    {
        Expression o = FoldReflectionExpr(expr: ix.Object);
        Expression i = FoldReflectionExpr(expr: ix.Index);
        if (ReferenceEquals(o, ix.Object) && ReferenceEquals(i, ix.Index)) return expr;
        var rw = ix with { Object = o, Index = i };
        rw.ResolvedType = ix.ResolvedType;
        rw.ResolvedSetItem = ix.ResolvedSetItem;
        return rw;
    }

    private Expression FoldTypeConversionExpr(Expression expr, TypeConversionExpression cv)
    {
        Expression e = FoldReflectionExpr(expr: cv.Expression);
        return ReferenceEquals(e, cv.Expression) ? expr : cv with { Expression = e };
    }

    private Expression FoldStealExpr(Expression expr, StealExpression st)
    {
        Expression o = FoldReflectionExpr(expr: st.Operand);
        return ReferenceEquals(o, st.Operand) ? expr : st with { Operand = o };
    }

    private Expression FoldGenericMemberRoutineCallExpr(Expression expr, GenericMemberRoutineCallExpression gmc)
    {
        Expression obj = FoldReflectionExpr(expr: gmc.Object);
        List<Expression> args = FoldReflectionList(list: gmc.Arguments);
        return !ReferenceEquals(obj, gmc.Object) || !ReferenceEquals(args, gmc.Arguments)
            ? gmc with { Object = obj, Arguments = args } : expr;
    }

    private Expression FoldGenericMemberExpr(Expression expr, GenericMemberExpression gm)
    {
        Expression o = FoldReflectionExpr(expr: gm.Object);
        return ReferenceEquals(o, gm.Object) ? expr : gm with { Object = o };
    }

    private Expression FoldIsPatternExpr(Expression expr, IsPatternExpression ip)
    {
        Expression e = FoldReflectionExpr(expr: ip.Expression);
        return ReferenceEquals(e, ip.Expression) ? expr : ip with { Expression = e };
    }

    private Expression FoldFlagsTestExpr(Expression expr, FlagsTestExpression ft)
    {
        Expression s = FoldReflectionExpr(expr: ft.Subject);
        return ReferenceEquals(s, ft.Subject) ? expr : ft with { Subject = s };
    }

    private Expression FoldChainedComparisonExpr(Expression expr, ChainedComparisonExpression ch)
    {
        List<Expression> ops = FoldReflectionList(list: ch.Operands);
        return ReferenceEquals(ops, ch.Operands) ? expr : ch with { Operands = ops };
    }

    private Expression FoldCompoundAssignmentExpr(Expression expr, CompoundAssignmentExpression cp)
    {
        Expression t = FoldReflectionExpr(expr: cp.Target);
        Expression v = FoldReflectionExpr(expr: cp.Value);
        return !ReferenceEquals(t, cp.Target) || !ReferenceEquals(v, cp.Value)
            ? cp with { Target = t, Value = v } : expr;
    }

    private Expression FoldRangeExpr(Expression expr, RangeExpression rg)
    {
        Expression s = FoldReflectionExpr(expr: rg.Start);
        Expression e = FoldReflectionExpr(expr: rg.End);
        Expression? st = rg.Step != null ? FoldReflectionExpr(expr: rg.Step) : null;
        return !ReferenceEquals(s, rg.Start) || !ReferenceEquals(e, rg.End) || !ReferenceEquals(st, rg.Step)
            ? rg with { Start = s, End = e, Step = st } : expr;
    }

    private Expression FoldConditionalExpr(Expression expr, ConditionalExpression co)
    {
        Expression c = FoldReflectionExpr(expr: co.Condition);
        Expression t = FoldReflectionExpr(expr: co.TrueExpression);
        Expression f = FoldReflectionExpr(expr: co.FalseExpression);
        return !ReferenceEquals(c, co.Condition) || !ReferenceEquals(t, co.TrueExpression)
               || !ReferenceEquals(f, co.FalseExpression)
            ? co with { Condition = c, TrueExpression = t, FalseExpression = f } : expr;
    }

    private Expression FoldTupleLiteralExpr(Expression expr, TupleLiteralExpression tp)
    {
        List<Expression> el = FoldReflectionList(list: tp.Elements);
        return ReferenceEquals(el, tp.Elements) ? expr : tp with { Elements = el };
    }

    private Expression FoldListLiteralExpr(Expression expr, ListLiteralExpression ll)
    {
        List<Expression> el = FoldReflectionList(list: ll.Elements);
        return ReferenceEquals(el, ll.Elements) ? expr : ll with { Elements = el };
    }

    private Expression FoldSetLiteralExpr(Expression expr, SetLiteralExpression se)
    {
        List<Expression> el = FoldReflectionList(list: se.Elements);
        return ReferenceEquals(el, se.Elements) ? expr : se with { Elements = el };
    }

    private Expression FoldDictLiteralExpr(Expression expr, DictLiteralExpression di)
    {
        bool changed = false;
        var pairs = new List<(Expression Key, Expression Value)>(capacity: di.Pairs.Count);
        foreach ((Expression k, Expression v) in di.Pairs)
        {
            Expression lk = FoldReflectionExpr(expr: k);
            Expression lv = FoldReflectionExpr(expr: v);
            pairs.Add(item: (lk, lv));
            if (!ReferenceEquals(lk, k) || !ReferenceEquals(lv, v)) changed = true;
        }
        return changed ? di with { Pairs = pairs } : expr;
    }

    private Expression FoldCreatorExpr(Expression expr, CreatorExpression cr)
    {
        bool changed = false;
        var mv = new List<(string Name, Expression Value)>(capacity: cr.MemberVariables.Count);
        foreach ((string name, Expression value) in cr.MemberVariables)
        {
            Expression v = FoldReflectionExpr(expr: value);
            mv.Add(item: (name, v));
            if (!ReferenceEquals(v, value)) changed = true;
        }
        return changed ? cr with { MemberVariables = mv } : expr;
    }

    private Expression FoldInsertedTextExpr(Expression expr, InsertedTextExpression fs)
    {
        bool changed = false;
        var parts = new List<InsertedTextPart>(capacity: fs.Parts.Count);
        foreach (InsertedTextPart part in fs.Parts)
        {
            if (part is ExpressionPart ep)
            {
                Expression e = FoldReflectionExpr(expr: ep.Expression);
                if (!ReferenceEquals(e, ep.Expression))
                {
                    parts.Add(item: ep with { Expression = e });
                    changed = true;
                    continue;
                }
            }
            parts.Add(item: part);
        }
        return changed ? fs with { Parts = parts } : expr;
    }

    private Expression FoldBlockExpr(Expression expr, BlockExpression bl)
    {
        Expression v = FoldReflectionExpr(expr: bl.Value);
        return ReferenceEquals(v, bl.Value) ? expr : bl with { Value = v };
    }

    private Expression FoldCarrierPayloadExpr(Expression expr, CarrierPayloadExpression cpe)
    {
        Expression c = FoldReflectionExpr(expr: cpe.Carrier);
        return ReferenceEquals(c, cpe.Carrier) ? expr : cpe with { Carrier = c };
    }

    private List<Expression> FoldReflectionList(List<Expression> list)
    {
        bool changed = false;
        var result = new List<Expression>(capacity: list.Count);
        foreach (Expression e in list)
        {
            Expression le = FoldReflectionExpr(expr: e);
            result.Add(item: le);
            if (!ReferenceEquals(le, e)) changed = true;
        }
        return changed ? result : list;
    }

    private ListLiteralExpression? FoldReflectionCall(TypeInfo owner, string routineName, TypeInfo? returnType,
        SourceLocation loc)
    {
        List<string>? values = ComputeReflectionStrings(owner: owner, routineName: routineName);
        if (values == null) return null;

        TypeInfo? textType = _registry.LookupType(name: "Text");
        if (textType == null) return null;

        var elements = values
            .Select(selector: s => (Expression)new LiteralExpression(Value: s,
                LiteralType: TokenType.TextLiteral, Location: loc) { ResolvedType = textType })
            .ToList();
        var literal = new ListLiteralExpression(Elements: elements, ElementType: null, Location: loc);
        // Analyze as a source `[...]` literal would be — sets ResolvedType (Owned[List[Text]]) AND resolves
        // the per-arity from_literal(Array[Text,N]) builder onto ResolvedLiteralBuilder so reachability seeds it.
        AnalyzeListLiteralExpression(list: literal, expectedType: returnType);
        return literal;
    }

    /// <summary>
    /// Recomputes the compile-time-constant string list a list-returning BuilderQuery reflection routine
    /// would return for <paramref name="owner"/>. MUST stay identical to the (now-removed) synthesized
    /// bodies in <c>WiredRoutinePass.TryHandleBuilderQueryConstant</c>. Returns null for a name that is not
    /// a constant list reflection routine.
    /// </summary>
    private List<string>? ComputeReflectionStrings(TypeInfo owner, string routineName)
    {
        switch (routineName)
        {
            case "protocols":
                return owner switch
                {
                    RecordTypeInfo r => r.ImplementedProtocols.Select(selector: p => p.Name).ToList(),
                    EntityTypeInfo e => e.ImplementedProtocols.Select(selector: p => p.Name).ToList(),
                    _ => new List<string>()
                };
            case "routine_names":
                return _registry.GetMemberRoutinesForType(type: owner)
                                .Select(selector: r => r.Name)
                                .Distinct()
                                .ToList();
            case "generic_args":
                return owner.TypeArguments?.Select(selector: t => t.Name).ToList()
                       ?? owner.GenericParameters?.ToList()
                       ?? new List<string>();
            case "annotations":
                return owner.Annotations?.ToList() ?? new List<string>();
            case "dependencies":
                return _registry.GetModuleDependencies(module: owner.Module).ToList();
            default:
                return null;
        }
    }
}
