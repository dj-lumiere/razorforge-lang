using Builder.Tokenizer;
using SyntaxTree;
using TypeModel.Types;

namespace Builder.Verification;

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
            Statement lowered = FoldReflectionStmt(stmt: _variantBodies[key: key]);
            if (!ReferenceEquals(objA: lowered, objB: _variantBodies[key: key]))
            {
                _variantBodies[key: key] = lowered;
            }
        }
    }

    private void FoldReflectionInPrograms(
        List<(Program Program, string FilePath, string Module)> programs)
    {
        foreach ((Program program, _, _) in programs)
        {
            for (int i = 0; i < program.Declarations.Count; i++)
            {
                switch (program.Declarations[index: i])
                {
                    case RoutineDeclaration r:
                    {
                        Statement nb = FoldReflectionStmt(stmt: r.Body);
                        if (!ReferenceEquals(objA: nb, objB: r.Body))
                        {
                            program.Declarations[index: i] = r with { Body = nb };
                        }

                        break;
                    }
                    case EntityDeclaration e: FoldReflectionMembers(members: e.Members); break;
                    case RecordDeclaration rec: FoldReflectionMembers(members: rec.Members); break;
                    case CrashableDeclaration cr:
                        FoldReflectionMembers(members: cr.Members); break;
                }
            }
        }
    }

    private void FoldReflectionMembers(List<SyntaxTree.Declaration> members)
    {
        for (int j = 0; j < members.Count; j++)
        {
            if (members[index: j] is not RoutineDeclaration m)
            {
                continue;
            }

            Statement nb = FoldReflectionStmt(stmt: m.Body);
            if (!ReferenceEquals(objA: nb, objB: m.Body))
            {
                members[index: j] = m with { Body = nb };
            }
        }
    }

    private Statement FoldReflectionStmt(Statement stmt)
    {
        return stmt switch
        {
            BlockStatement b => FoldBlockStmt(stmt: stmt, b: b),
            IfStatement ifs => FoldIfStmt(stmt: stmt, ifs: ifs),
            WhileStatement w => FoldWhileStmt(stmt: stmt, w: w),
            LoopStatement loop => FoldLoopStmt(stmt: stmt, loop: loop),
            EachStatement f => FoldEachStmt(stmt: stmt, f: f),
            WhenStatement ws => FoldWhenStmt(stmt: stmt, ws: ws),
            ReturnStatement { Value: not null } ret => FoldReturnStmt(stmt: stmt, ret: ret),
            AssignmentStatement asg => FoldAssignmentStmt(stmt: stmt, asg: asg),
            DeclarationStatement
            {
                Declaration: VariableDeclaration { Initializer: not null } vd
            } ds => FoldDeclarationStmt(stmt: stmt, ds: ds, vd: vd),
            ExpressionStatement es => FoldExpressionStmt(stmt: stmt, es: es),
            DiscardStatement dsc => FoldDiscardStmt(stmt: stmt, dsc: dsc),
            ThrowStatement ts => FoldThrowStmt(stmt: stmt, ts: ts),
            BecomesStatement bs => FoldBecomesStmt(stmt: stmt, bs: bs),
            UsingStatement us => FoldUsingStmt(stmt: stmt, us: us),
            DangerStatement dg => FoldDangerStmt(stmt: stmt, dg: dg),
            _ => stmt
        };
    }

    private Statement FoldBlockStmt(Statement stmt, BlockStatement b)
    {
        bool changed = false;
        var list = new List<Statement>(capacity: b.Statements.Count);
        foreach (Statement s in b.Statements)
        {
            Statement ns = FoldReflectionStmt(stmt: s);
            list.Add(item: ns);
            if (!ReferenceEquals(objA: ns, objB: s))
            {
                changed = true;
            }
        }

        return changed
            ? b with { Statements = list }
            : stmt;
    }

    private Statement FoldIfStmt(Statement stmt, IfStatement ifs)
    {
        Expression c = FoldReflectionExpr(expr: ifs.Condition);
        Statement t = FoldReflectionStmt(stmt: ifs.ThenStatement);
        Statement? e = ifs.ElseStatement != null
            ? FoldReflectionStmt(stmt: ifs.ElseStatement)
            : null;
        return !ReferenceEquals(objA: c, objB: ifs.Condition) ||
               !ReferenceEquals(objA: t, objB: ifs.ThenStatement) ||
               !ReferenceEquals(objA: e, objB: ifs.ElseStatement)
            ? ifs with { Condition = c, ThenStatement = t, ElseStatement = e }
            : stmt;
    }

    private Statement FoldWhileStmt(Statement stmt, WhileStatement w)
    {
        Expression c = FoldReflectionExpr(expr: w.Condition);
        Statement body = FoldReflectionStmt(stmt: w.Body);
        return !ReferenceEquals(objA: c, objB: w.Condition) ||
               !ReferenceEquals(objA: body, objB: w.Body)
            ? w with { Condition = c, Body = body }
            : stmt;
    }

    private Statement FoldLoopStmt(Statement stmt, LoopStatement loop)
    {
        Statement body = FoldReflectionStmt(stmt: loop.Body);
        return ReferenceEquals(objA: body, objB: loop.Body)
            ? stmt
            : loop with { Body = body };
    }

    private Statement FoldEachStmt(Statement stmt, EachStatement f)
    {
        Expression it = FoldReflectionExpr(expr: f.Iterable);
        Statement body = FoldReflectionStmt(stmt: f.Body);
        return !ReferenceEquals(objA: it, objB: f.Iterable) ||
               !ReferenceEquals(objA: body, objB: f.Body)
            ? f with { Iterable = it, Body = body }
            : stmt;
    }

    private Statement FoldWhenStmt(Statement stmt, WhenStatement ws)
    {
        Expression subj = FoldReflectionExpr(expr: ws.Expression);
        bool changed = !ReferenceEquals(objA: subj, objB: ws.Expression);
        var clauses = new List<WhenClause>(capacity: ws.Clauses.Count);
        foreach (WhenClause cl in ws.Clauses)
        {
            Statement cb = FoldReflectionStmt(stmt: cl.Body);
            if (!ReferenceEquals(objA: cb, objB: cl.Body))
            {
                changed = true;
            }

            clauses.Add(item: !ReferenceEquals(objA: cb, objB: cl.Body)
                ? cl with { Body = cb }
                : cl);
        }

        return changed
            ? ws with { Expression = subj, Clauses = clauses }
            : stmt;
    }

    private Statement FoldReturnStmt(Statement stmt, ReturnStatement ret)
    {
        Expression retValue = ret.Value!;
        Expression v = FoldReflectionExpr(expr: retValue);
        return ReferenceEquals(objA: v, objB: retValue)
            ? stmt
            : ret with { Value = v };
    }

    private Statement FoldAssignmentStmt(Statement stmt, AssignmentStatement asg)
    {
        Expression v = FoldReflectionExpr(expr: asg.Value);
        return ReferenceEquals(objA: v, objB: asg.Value)
            ? stmt
            : asg with { Value = v };
    }

    private Statement FoldDeclarationStmt(Statement stmt, DeclarationStatement ds,
        VariableDeclaration vd)
    {
        Expression vdInitializer = vd.Initializer!;
        Expression init = FoldReflectionExpr(expr: vdInitializer);
        return ReferenceEquals(objA: init, objB: vdInitializer)
            ? stmt
            : ds with { Declaration = vd with { Initializer = init } };
    }

    private Statement FoldExpressionStmt(Statement stmt, ExpressionStatement es)
    {
        Expression e = FoldReflectionExpr(expr: es.Expression);
        return ReferenceEquals(objA: e, objB: es.Expression)
            ? stmt
            : es with { Expression = e };
    }

    private Statement FoldDiscardStmt(Statement stmt, DiscardStatement dsc)
    {
        Expression e = FoldReflectionExpr(expr: dsc.Expression);
        return ReferenceEquals(objA: e, objB: dsc.Expression)
            ? stmt
            : dsc with { Expression = e };
    }

    private Statement FoldThrowStmt(Statement stmt, ThrowStatement ts)
    {
        Expression e = FoldReflectionExpr(expr: ts.Error);
        return ReferenceEquals(objA: e, objB: ts.Error)
            ? stmt
            : ts with { Error = e };
    }

    private Statement FoldBecomesStmt(Statement stmt, BecomesStatement bs)
    {
        Expression v = FoldReflectionExpr(expr: bs.Value);
        return ReferenceEquals(objA: v, objB: bs.Value)
            ? stmt
            : bs with { Value = v };
    }

    private Statement FoldUsingStmt(Statement stmt, UsingStatement us)
    {
        Statement body = FoldReflectionStmt(stmt: us.Body);
        Statement? fb = us.FallbackBody != null
            ? FoldReflectionStmt(stmt: us.FallbackBody)
            : null;
        return ReferenceEquals(objA: body, objB: us.Body) &&
               ReferenceEquals(objA: fb, objB: us.FallbackBody)
            ? stmt
            : us with { Body = body, FallbackBody = fb };
    }

    private Statement FoldDangerStmt(Statement stmt, DangerStatement dg)
    {
        Statement nb = FoldReflectionStmt(stmt: dg.Body);
        return !ReferenceEquals(objA: nb, objB: dg.Body) && nb is BlockStatement bs2
            ? dg with { Body = bs2 }
            : stmt;
    }

    private Expression FoldReflectionExpr(Expression expr)
    {
        // Leaf fold: a 0-arg member call to a constant list-returning BuilderQuery reflection routine on a
        // concrete receiver → inline analyzed List[Text] literal.
        if (expr is CallExpression
            {
                Callee: MemberExpression { MemberName: var rn, Object: { } recv },
                Arguments: { Count: 0 }
            } bqCall && BuilderInfoProvider.IsListReturningConstantRoutine(name: rn) &&
            recv.ResolvedType is { } owner && owner is not GenericParameterTypeSymbol &&
            !owner.IsGenericDefinition)
        {
            ListLiteralExpression? folded = FoldReflectionCall(owner: owner,
                routineName: rn,
                returnType: bqCall.ResolvedRoutine?.ReturnType,
                loc: bqCall.Location);
            if (folded != null)
            {
                return folded;
            }
        }

        Expression result = FoldReflectionExprStructural(expr: expr);
        // Record with-expressions copy only primary-constructor properties, not the mutable ResolvedType
        // that semantic analysis annotated. A structural rebuild therefore drops ResolvedType, and the
        // expression-lowering pass later throws "reached without a resolved type". The rebuilt node
        // carries the same logical expression and type, so restore it here. Cases that already set a
        // specific type (folded call or index nodes) leave ResolvedType non-null and are untouched.
        if (!ReferenceEquals(objA: result, objB: expr) && result.ResolvedType is null)
        {
            result.ResolvedType = expr.ResolvedType;
        }

        return result;
    }

    private Expression FoldReflectionExprStructural(Expression expr)
    {
        return expr switch
        {
            BinaryExpression bin => FoldBinaryExpr(expr: expr, bin: bin),
            UnaryExpression un => FoldUnaryExpr(expr: expr, un: un),
            CallExpression call => FoldCallExpr(expr: expr, call: call),
            NamedArgumentExpression na => FoldNamedArgumentExpr(expr: expr, na: na),
            MemberExpression mem => FoldMemberExpr(expr: expr, mem: mem),
            OptionalMemberExpression om => FoldOptionalMemberExpr(expr: expr, om: om),
            IndexExpression ix => FoldIndexExpr(expr: expr, ix: ix),
            TypeConversionExpression cv => FoldTypeConversionExpr(expr: expr, cv: cv),
            StealExpression st => FoldStealExpr(expr: expr, st: st),
            GenericMemberRoutineCallExpression gmc => FoldGenericMemberRoutineCallExpr(expr: expr,
                gmc: gmc),
            GenericMemberExpression gm => FoldGenericMemberExpr(expr: expr, gm: gm),
            IsPatternExpression ip => FoldIsPatternExpr(expr: expr, ip: ip),
            FlagsTestExpression ft => FoldFlagsTestExpr(expr: expr, ft: ft),
            ChainedComparisonExpression ch => FoldChainedComparisonExpr(expr: expr, ch: ch),
            CompoundAssignmentExpression cp => FoldCompoundAssignmentExpr(expr: expr, cp: cp),
            RangeExpression rg => FoldRangeExpr(expr: expr, rg: rg),
            ConditionalExpression co => FoldConditionalExpr(expr: expr, co: co),
            TupleLiteralExpression tp => FoldTupleLiteralExpr(expr: expr, tp: tp),
            ListLiteralExpression ll => FoldListLiteralExpr(expr: expr, ll: ll),
            SetLiteralExpression se => FoldSetLiteralExpr(expr: expr, se: se),
            DictLiteralExpression di => FoldDictLiteralExpr(expr: expr, di: di),
            CreatorExpression cr => FoldCreatorExpr(expr: expr, cr: cr),
            InsertedTextExpression fs => FoldInsertedTextExpr(expr: expr, fs: fs),
            BlockExpression bl => FoldBlockExpr(expr: expr, bl: bl),
            CarrierPayloadExpression cpe => FoldCarrierPayloadExpr(expr: expr, cpe: cpe),
            _ => expr
        };
    }

    private Expression FoldBinaryExpr(Expression expr, BinaryExpression bin)
    {
        Expression l = FoldReflectionExpr(expr: bin.Left);
        Expression r = FoldReflectionExpr(expr: bin.Right);
        return ReferenceEquals(objA: l, objB: bin.Left) &&
               ReferenceEquals(objA: r, objB: bin.Right)
            ? expr
            : bin with { Left = l, Right = r };
    }

    private Expression FoldUnaryExpr(Expression expr, UnaryExpression un)
    {
        Expression o = FoldReflectionExpr(expr: un.Operand);
        return ReferenceEquals(objA: o, objB: un.Operand)
            ? expr
            : un with { Operand = o };
    }

    private Expression FoldCallExpr(Expression expr, CallExpression call)
    {
        Expression callee = FoldReflectionExpr(expr: call.Callee);
        List<Expression> args = FoldReflectionList(list: call.Arguments);
        if (ReferenceEquals(objA: callee, objB: call.Callee) &&
            ReferenceEquals(objA: args, objB: call.Arguments))
        {
            return expr;
        }

        CallExpression rw = call with { Callee = callee, Arguments = args };
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
        return ReferenceEquals(objA: v, objB: na.Value)
            ? expr
            : na with { Value = v };
    }

    private Expression FoldMemberExpr(Expression expr, MemberExpression mem)
    {
        Expression o = FoldReflectionExpr(expr: mem.Object);
        return ReferenceEquals(objA: o, objB: mem.Object)
            ? expr
            : mem with { Object = o };
    }

    private Expression FoldOptionalMemberExpr(Expression expr, OptionalMemberExpression om)
    {
        Expression o = FoldReflectionExpr(expr: om.Object);
        return ReferenceEquals(objA: o, objB: om.Object)
            ? expr
            : om with { Object = o };
    }

    private Expression FoldIndexExpr(Expression expr, IndexExpression ix)
    {
        Expression o = FoldReflectionExpr(expr: ix.Object);
        Expression i = FoldReflectionExpr(expr: ix.Index);
        if (ReferenceEquals(objA: o, objB: ix.Object) && ReferenceEquals(objA: i, objB: ix.Index))
        {
            return expr;
        }

        IndexExpression rw = ix with { Object = o, Index = i };
        rw.ResolvedType = ix.ResolvedType;
        rw.ResolvedSetItem = ix.ResolvedSetItem;
        return rw;
    }

    private Expression FoldTypeConversionExpr(Expression expr, TypeConversionExpression cv)
    {
        Expression e = FoldReflectionExpr(expr: cv.Expression);
        return ReferenceEquals(objA: e, objB: cv.Expression)
            ? expr
            : cv with { Expression = e };
    }

    private Expression FoldStealExpr(Expression expr, StealExpression st)
    {
        Expression o = FoldReflectionExpr(expr: st.Operand);
        return ReferenceEquals(objA: o, objB: st.Operand)
            ? expr
            : st with { Operand = o };
    }

    private Expression FoldGenericMemberRoutineCallExpr(Expression expr,
        GenericMemberRoutineCallExpression gmc)
    {
        Expression obj = FoldReflectionExpr(expr: gmc.Object);
        List<Expression> args = FoldReflectionList(list: gmc.Arguments);
        return !ReferenceEquals(objA: obj, objB: gmc.Object) ||
               !ReferenceEquals(objA: args, objB: gmc.Arguments)
            ? gmc with { Object = obj, Arguments = args }
            : expr;
    }

    private Expression FoldGenericMemberExpr(Expression expr, GenericMemberExpression gm)
    {
        Expression o = FoldReflectionExpr(expr: gm.Object);
        return ReferenceEquals(objA: o, objB: gm.Object)
            ? expr
            : gm with { Object = o };
    }

    private Expression FoldIsPatternExpr(Expression expr, IsPatternExpression ip)
    {
        Expression e = FoldReflectionExpr(expr: ip.Expression);
        return ReferenceEquals(objA: e, objB: ip.Expression)
            ? expr
            : ip with { Expression = e };
    }

    private Expression FoldFlagsTestExpr(Expression expr, FlagsTestExpression ft)
    {
        Expression s = FoldReflectionExpr(expr: ft.Subject);
        return ReferenceEquals(objA: s, objB: ft.Subject)
            ? expr
            : ft with { Subject = s };
    }

    private Expression FoldChainedComparisonExpr(Expression expr, ChainedComparisonExpression ch)
    {
        List<Expression> ops = FoldReflectionList(list: ch.Operands);
        return ReferenceEquals(objA: ops, objB: ch.Operands)
            ? expr
            : ch with { Operands = ops };
    }

    private Expression FoldCompoundAssignmentExpr(Expression expr, CompoundAssignmentExpression cp)
    {
        Expression t = FoldReflectionExpr(expr: cp.Target);
        Expression v = FoldReflectionExpr(expr: cp.Value);
        return !ReferenceEquals(objA: t, objB: cp.Target) ||
               !ReferenceEquals(objA: v, objB: cp.Value)
            ? cp with { Target = t, Value = v }
            : expr;
    }

    private Expression FoldRangeExpr(Expression expr, RangeExpression rg)
    {
        Expression s = FoldReflectionExpr(expr: rg.Start);
        Expression e = FoldReflectionExpr(expr: rg.End);
        Expression? st = rg.Step != null
            ? FoldReflectionExpr(expr: rg.Step)
            : null;
        return !ReferenceEquals(objA: s, objB: rg.Start) ||
               !ReferenceEquals(objA: e, objB: rg.End) || !ReferenceEquals(objA: st, objB: rg.Step)
            ? rg with { Start = s, End = e, Step = st }
            : expr;
    }

    private Expression FoldConditionalExpr(Expression expr, ConditionalExpression co)
    {
        Expression c = FoldReflectionExpr(expr: co.Condition);
        Expression t = FoldReflectionExpr(expr: co.TrueExpression);
        Expression f = FoldReflectionExpr(expr: co.FalseExpression);
        return !ReferenceEquals(objA: c, objB: co.Condition) ||
               !ReferenceEquals(objA: t, objB: co.TrueExpression) ||
               !ReferenceEquals(objA: f, objB: co.FalseExpression)
            ? co with { Condition = c, TrueExpression = t, FalseExpression = f }
            : expr;
    }

    private Expression FoldTupleLiteralExpr(Expression expr, TupleLiteralExpression tp)
    {
        List<Expression> el = FoldReflectionList(list: tp.Elements);
        return ReferenceEquals(objA: el, objB: tp.Elements)
            ? expr
            : tp with { Elements = el };
    }

    private Expression FoldListLiteralExpr(Expression expr, ListLiteralExpression ll)
    {
        List<Expression> el = FoldReflectionList(list: ll.Elements);
        return ReferenceEquals(objA: el, objB: ll.Elements)
            ? expr
            : ll with { Elements = el };
    }

    private Expression FoldSetLiteralExpr(Expression expr, SetLiteralExpression se)
    {
        List<Expression> el = FoldReflectionList(list: se.Elements);
        return ReferenceEquals(objA: el, objB: se.Elements)
            ? expr
            : se with { Elements = el };
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
            if (!ReferenceEquals(objA: lk, objB: k) || !ReferenceEquals(objA: lv, objB: v))
            {
                changed = true;
            }
        }

        return changed
            ? di with { Pairs = pairs }
            : expr;
    }

    private Expression FoldCreatorExpr(Expression expr, CreatorExpression cr)
    {
        bool changed = false;
        var mv = new List<(string Name, Expression Value)>(capacity: cr.MemberVariables.Count);
        foreach ((string name, Expression value) in cr.MemberVariables)
        {
            Expression v = FoldReflectionExpr(expr: value);
            mv.Add(item: (name, v));
            if (!ReferenceEquals(objA: v, objB: value))
            {
                changed = true;
            }
        }

        return changed
            ? cr with { MemberVariables = mv }
            : expr;
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
                if (!ReferenceEquals(objA: e, objB: ep.Expression))
                {
                    parts.Add(item: ep with { Expression = e });
                    changed = true;
                    continue;
                }
            }

            parts.Add(item: part);
        }

        return changed
            ? fs with { Parts = parts }
            : expr;
    }

    private Expression FoldBlockExpr(Expression expr, BlockExpression bl)
    {
        Expression v = FoldReflectionExpr(expr: bl.Value);
        return ReferenceEquals(objA: v, objB: bl.Value)
            ? expr
            : bl with { Value = v };
    }

    private Expression FoldCarrierPayloadExpr(Expression expr, CarrierPayloadExpression cpe)
    {
        Expression c = FoldReflectionExpr(expr: cpe.Carrier);
        return ReferenceEquals(objA: c, objB: cpe.Carrier)
            ? expr
            : cpe with { Carrier = c };
    }

    private List<Expression> FoldReflectionList(List<Expression> list)
    {
        bool changed = false;
        var result = new List<Expression>(capacity: list.Count);
        foreach (Expression e in list)
        {
            Expression le = FoldReflectionExpr(expr: e);
            result.Add(item: le);
            if (!ReferenceEquals(objA: le, objB: e))
            {
                changed = true;
            }
        }

        return changed
            ? result
            : list;
    }

    private ListLiteralExpression? FoldReflectionCall(TypeSymbol owner, string routineName,
        TypeSymbol? returnType, SourceLocation loc)
    {
        List<string>? values = ComputeReflectionStrings(owner: owner, routineName: routineName);
        if (values == null)
        {
            return null;
        }

        TypeSymbol? textType = _registry.LookupType(name: "Text");
        if (textType == null)
        {
            return null;
        }

        var elements = values.Select(selector: s =>
                                  (Expression)new LiteralExpression(Value: s,
                                      LiteralType: TokenType.TextLiteral,
                                      Location: loc) { ResolvedType = textType })
                             .ToList();
        var literal =
            new ListLiteralExpression(Elements: elements, ElementType: null, Location: loc);
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
    private List<string>? ComputeReflectionStrings(TypeSymbol owner, string routineName)
    {
        return routineName switch
        {
            "protocols" => owner switch
            {
                RecordTypeSymbol r => r.ImplementedProtocols
                                     .Select(selector: p => p.Name)
                                     .ToList(),
                EntityTypeSymbol e => e.ImplementedProtocols
                                     .Select(selector: p => p.Name)
                                     .ToList(),
                _ => new List<string>()
            },
            "routine_names" => _registry.GetMemberRoutinesForType(type: owner)
                                        .Select(selector: r => r.Name)
                                        .Distinct()
                                        .ToList(),
            "generic_args" => owner.TypeArguments
                                  ?.Select(selector: t => t.Name)
                                   .ToList() ?? owner.GenericParameters?.ToList() ??
                new List<string>(),
            "annotations" => owner.Annotations?.ToList() ?? new List<string>(),
            "dependencies" => _registry.GetModuleDependencies(module: owner.Module)
                                       .ToList(),
            _ => null
        };
    }
}
