using SyntaxTree;
using TypeModel.Types;
using TypeModel.Symbols;
using Compiler.Declaration;

namespace Compiler.Desugaring.Passes;

/// <summary>
/// Walks a (deep-cloned) statement and rewires every reference to a specific
/// binding name so it dispatches against a concrete type instead of the
/// abstract protocol type from which the binding was originally typed.
///
/// Used by <see cref="CrashableExpansionPass"/> after fanning out
/// <c>is Crashable err =&gt; body</c> into one TypePattern arm per registered
/// crashable type — each arm gets its own clone of <c>body</c> with
/// <c>err</c>'s <c>ResolvedType</c> set to that arm's concrete crashable, and
/// every <c>err.MemberRoutine(...)</c> re-resolved against the concrete type.
/// </summary>
internal sealed class BindingTypeRewriter : ISyntaxTreeVisitor<bool>
{
    private readonly string _bindingName;
    private readonly TypeInfo _concreteType;
    private readonly TypeRegistry _registry;

    private BindingTypeRewriter(string bindingName, TypeInfo concreteType, TypeRegistry registry)
    {
        _bindingName = bindingName;
        _concreteType = concreteType;
        _registry = registry;
    }

    public static void Apply(Statement body, string bindingName, TypeInfo concreteType,
        TypeRegistry registry)
    {
        var v = new BindingTypeRewriter(bindingName: bindingName, concreteType: concreteType,
            registry: registry);
        body.Accept(visitor: v);
    }

    private void Visit(Expression? e) => e?.Accept(visitor: this);
    private void Visit(Statement? s) => s?.Accept(visitor: this);
    private void VisitAll(IEnumerable<Expression>? xs)
    {
        if (xs == null) return;
        foreach (Expression x in xs) Visit(e: x);
    }
    private void VisitAll(IEnumerable<Statement>? xs)
    {
        if (xs == null) return;
        foreach (Statement x in xs) Visit(s: x);
    }

    public bool VisitIdentifierExpression(IdentifierExpression node)
    {
        if (node.Name == _bindingName) node.ResolvedType = _concreteType;
        return false;
    }

    public bool VisitCallExpression(CallExpression node)
    {
        // err.MemberRoutine(args) -> re-resolve ResolvedRoutine on concrete crashable.
        if (node.Callee is MemberExpression { Object: IdentifierExpression id } me
            && id.Name == _bindingName)
        {
            bool? isFailable = node.ResolvedRoutine?.IsFailable;
            RoutineInfo? resolved = _registry.LookupMemberRoutine(type: _concreteType,
                memberRoutineName: me.MemberName, isFailable: isFailable);
            if (resolved != null)
            {
                node.ResolvedRoutine = resolved;
                me.ResolvedType = resolved.ReturnType;
                node.ResolvedType = resolved.ReturnType;
            }
            // Still rewire the receiver identifier's ResolvedType.
            id.ResolvedType = _concreteType;
        }

        Visit(e: node.Callee);
        VisitAll(xs: node.Arguments);
        return false;
    }

    public bool VisitMemberExpression(MemberExpression node)
    {
        Visit(e: node.Object);
        return false;
    }

    public bool VisitSpliceExpression(SpliceExpression node)
    {
        Visit(e: node.Inner);
        return false;
    }

    public bool VisitSpliceMemberExpression(SpliceMemberExpression node)
    {
        Visit(e: node.Object);
        return false;
    }

    // ---- pass-throughs ----
    public bool VisitLiteralExpression(LiteralExpression node) => false;
    public bool VisitTypeExpression(TypeExpression node) => false;
    public bool VisitTypeIdExpression(TypeIdExpression node) => false;
    public bool VisitBreakStatement(BreakStatement node) => false;
    public bool VisitContinueStatement(ContinueStatement node) => false;
    public bool VisitAbsentStatement(AbsentStatement node) => false;
    public bool VisitPassStatement(PassStatement node) => false;

    public bool VisitListLiteralExpression(ListLiteralExpression node)
    { VisitAll(xs: node.Elements); return false; }
    public bool VisitSetLiteralExpression(SetLiteralExpression node)
    { VisitAll(xs: node.Elements); return false; }
    public bool VisitTupleLiteralExpression(TupleLiteralExpression node)
    { VisitAll(xs: node.Elements); return false; }
    public bool VisitDictLiteralExpression(DictLiteralExpression node)
    {
        foreach (var p in node.Pairs) { Visit(e: p.Key); Visit(e: p.Value); }
        return false;
    }
    public bool VisitDictEntryLiteralExpression(DictEntryLiteralExpression node)
    { Visit(e: node.Key); Visit(e: node.Value); return false; }

    public bool VisitCompoundAssignmentExpression(CompoundAssignmentExpression node)
    { Visit(e: node.Target); Visit(e: node.Value); return false; }
    public bool VisitBinaryExpression(BinaryExpression node)
    { Visit(e: node.Left); Visit(e: node.Right); return false; }
    public bool VisitUnaryExpression(UnaryExpression node)
    { Visit(e: node.Operand); return false; }
    public bool VisitNamedArgumentExpression(NamedArgumentExpression node)
    { Visit(e: node.Value); return false; }
    public bool VisitCreatorExpression(CreatorExpression node)
    {
        foreach (var mv in node.MemberVariables) Visit(e: mv.Value);
        return false;
    }
    public bool VisitWithExpression(WithExpression node)
    {
        Visit(e: node.Base);
        foreach (var u in node.Updates) { Visit(e: u.Value); Visit(e: u.Index); }
        return false;
    }
    public bool VisitOptionalMemberExpression(OptionalMemberExpression node)
    { Visit(e: node.Object); return false; }
    public bool VisitIndexExpression(IndexExpression node)
    { Visit(e: node.Object); Visit(e: node.Index); return false; }
    public bool VisitConditionalExpression(ConditionalExpression node)
    { Visit(e: node.Condition); Visit(e: node.TrueExpression); Visit(e: node.FalseExpression); return false; }
    public bool VisitBlockExpression(BlockExpression node)
    { Visit(e: node.Value); return false; }
    public bool VisitChainedComparisonExpression(ChainedComparisonExpression node)
    { VisitAll(xs: node.Operands); return false; }
    public bool VisitRangeExpression(RangeExpression node)
    { Visit(e: node.Start); Visit(e: node.End); Visit(e: node.Step); return false; }
    public bool VisitLambdaExpression(LambdaExpression node)
    { Visit(e: node.Body); return false; }
    public bool VisitTypeConversionExpression(TypeConversionExpression node)
    { Visit(e: node.Expression); return false; }
    public bool VisitGenericMemberRoutineCallExpression(GenericMemberRoutineCallExpression node)
    { Visit(e: node.Object); VisitAll(xs: node.Arguments); return false; }
    public bool VisitGenericMemberExpression(GenericMemberExpression node)
    { Visit(e: node.Object); return false; }
    public bool VisitBracketAccessExpression(BracketAccessExpression node) =>
        throw new InvalidOperationException(
            "BracketAccessExpression must be lowered by BracketReclassifyPass before analysis.");
    public bool VisitCarrierPayloadExpression(CarrierPayloadExpression node)
    { Visit(e: node.Carrier); return false; }
    public bool VisitCrashableDispatchExpression(CrashableDispatchExpression node)
    { Visit(e: node.Carrier); return false; }
    public bool VisitIsPatternExpression(IsPatternExpression node)
    { Visit(e: node.Expression); VisitPattern(p: node.Pattern); return false; }
    public bool VisitFlagsTestExpression(FlagsTestExpression node)
    { Visit(e: node.Subject); return false; }
    public bool VisitWhenExpression(WhenExpression node)
    {
        Visit(e: node.Expression);
        foreach (var c in node.Clauses) { VisitPattern(p: c.Pattern); Visit(s: c.Body); }
        return false;
    }
    public bool VisitStealExpression(StealExpression node)
    { Visit(e: node.Operand); return false; }
    public bool VisitWaitforExpression(WaitforExpression node)
    { Visit(e: node.Operand); Visit(e: node.Timeout); return false; }
    public bool VisitDependentWaitforExpression(DependentWaitforExpression node)
    { Visit(e: node.Operand); Visit(e: node.Timeout); return false; }
    public bool VisitBackIndexExpression(BackIndexExpression node)
    { Visit(e: node.Operand); return false; }
    public bool VisitInsertedTextExpression(InsertedTextExpression node)
    {
        foreach (var p in node.Parts.OfType<ExpressionPart>()) Visit(e: p.Expression);
        return false;
    }

    public bool VisitExpressionStatement(ExpressionStatement node)
    { Visit(e: node.Expression); return false; }
    public bool VisitDeclarationStatement(DeclarationStatement node)
    {
        if (node.Declaration is VariableDeclaration { Initializer: not null } v)
            Visit(e: v.Initializer);
        return false;
    }
    public bool VisitAssignmentStatement(AssignmentStatement node)
    { Visit(e: node.Target); Visit(e: node.Value); return false; }
    public bool VisitDestructuringStatement(DestructuringStatement node)
    { Visit(e: node.Initializer); return false; }
    public bool VisitReturnStatement(ReturnStatement node)
    { Visit(e: node.Value); return false; }
    public bool VisitBecomesStatement(BecomesStatement node)
    { Visit(e: node.Value); return false; }
    public bool VisitVariantReturnStatement(VariantReturnStatement node)
    { Visit(e: node.Value); return false; }
    public bool VisitThrowStatement(ThrowStatement node)
    { Visit(e: node.Error); return false; }
    public bool VisitIfStatement(IfStatement node)
    { Visit(e: node.Condition); Visit(s: node.ThenStatement); Visit(s: node.ElseStatement); return false; }
    public bool VisitWhileStatement(WhileStatement node)
    { Visit(e: node.Condition); Visit(s: node.Body); Visit(s: node.ElseBranch); return false; }
    public bool VisitLoopStatement(LoopStatement node)
    { Visit(s: node.Body); return false; }
    public bool VisitExpandStatement(ExpandStatement node)
    { Visit(s: node.Body); return false; }
    public bool VisitEachStatement(EachStatement node)
    { Visit(e: node.Iterable); Visit(s: node.Body); Visit(s: node.ElseBranch); return false; }
    public bool VisitBlockStatement(BlockStatement node)
    { VisitAll(xs: node.Statements); return false; }
    public bool VisitWhenStatement(WhenStatement node)
    {
        Visit(e: node.Expression);
        foreach (var c in node.Clauses) { VisitPattern(p: c.Pattern); Visit(s: c.Body); }
        if (node.ArmExpansion != null) Visit(s: node.ArmExpansion.Template.Body);
        return false;
    }
    public bool VisitDangerStatement(DangerStatement node)
    { Visit(s: node.Body); return false; }
    public bool VisitUsingStatement(UsingStatement node)
    {
        Visit(e: node.Resource);
        Visit(s: node.Body);
        if (node.FallbackBody != null)
        {
            Visit(s: node.FallbackBody);
        }

        return false;
    }
    public bool VisitDiscardStatement(DiscardStatement node)
    { Visit(e: node.Expression); return false; }

    public bool VisitVariableDeclaration(VariableDeclaration node)
    { Visit(e: node.Initializer); return false; }

    public bool VisitExpandMemberDeclaration(ExpandMemberDeclaration node) => false;

    // Nested decls: don't recurse — they introduce their own scope and any `err`
    // reference inside them shadows / refers elsewhere.
    public bool VisitFunctionDeclaration(RoutineDeclaration node) => false;
    public bool VisitEntityDeclaration(EntityDeclaration node) => false;
    public bool VisitRecordDeclaration(RecordDeclaration node) => false;
    public bool VisitChoiceDeclaration(ChoiceDeclaration node) => false;
    public bool VisitFlagsDeclaration(FlagsDeclaration node) => false;
    public bool VisitCrashableDeclaration(CrashableDeclaration node) => false;
    public bool VisitVariantDeclaration(VariantDeclaration node) => false;
    public bool VisitProtocolDeclaration(ProtocolDeclaration node) => false;
    public bool VisitImportDeclaration(ImportDeclaration node) => false;
    public bool VisitModuleDeclaration(ModuleDeclaration node) => false;
    public bool VisitDefineDeclaration(DefineDeclaration node) => false;
    public bool VisitExternalDeclaration(ExternalDeclaration node) => false;
    public bool VisitExternalBlockDeclaration(ExternalBlockDeclaration node) => false;
    public bool VisitPresetDeclaration(PresetDeclaration node) => false;
    public bool VisitProgram(Program node) => false;

    private void VisitPattern(Pattern? p)
    {
        switch (p)
        {
            case ExpressionPattern e: Visit(e: e.Expression); break;
            case ComparisonPattern c: Visit(e: c.Value); break;
            case GuardPattern g: VisitPattern(p: g.InnerPattern); Visit(e: g.Guard); break;
        }
    }
}