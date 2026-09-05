using System.Collections.Generic;
using System.Linq;
using SyntaxTree;

namespace Compiler.CodeGen;

/// <summary>
/// Returns <c>true</c> on the first descendant that is an
/// <see cref="IdentifierExpression"/> with <c>Name == "me"</c>.
/// Used to decide whether an entity <c>create</c> routine needs an implicit
/// <c>me</c> allocation at routine entry — canonical <c>return Type(field: …)</c>
/// factories that never touch <c>me</c> skip the extra heap allocation.
/// </summary>
internal sealed class MeReferenceScanner : ISyntaxTreeVisitor<bool>
{
    public static bool Scan(Statement body) => body.Accept(visitor: new MeReferenceScanner());

    private bool Any(IEnumerable<Expression>? xs) =>
        xs != null && xs.Any(predicate: x => x.Accept(visitor: this));

    private bool AnyStmt(IEnumerable<Statement>? xs) =>
        xs != null && xs.Any(predicate: x => x.Accept(visitor: this));

    public bool VisitIdentifierExpression(IdentifierExpression node) => node.Name == "me";

    public bool VisitLiteralExpression(LiteralExpression node) => false;
    public bool VisitTypeExpression(TypeExpression node) => false;
    public bool VisitTypeIdExpression(TypeIdExpression node) => false;
    public bool VisitBreakStatement(BreakStatement node) => false;
    public bool VisitContinueStatement(ContinueStatement node) => false;
    public bool VisitAbsentStatement(AbsentStatement node) => false;
    public bool VisitPassStatement(PassStatement node) => false;

    public bool VisitListLiteralExpression(ListLiteralExpression node) => Any(xs: node.Elements);
    public bool VisitSetLiteralExpression(SetLiteralExpression node) => Any(xs: node.Elements);
    public bool VisitTupleLiteralExpression(TupleLiteralExpression node) => Any(xs: node.Elements);
    public bool VisitDictLiteralExpression(DictLiteralExpression node) =>
        node.Pairs.Any(predicate: e =>
            e.Key.Accept(visitor: this) || e.Value.Accept(visitor: this));
    public bool VisitDictEntryLiteralExpression(DictEntryLiteralExpression node) =>
        node.Key.Accept(visitor: this) || node.Value.Accept(visitor: this);

    public bool VisitCompoundAssignmentExpression(CompoundAssignmentExpression node) =>
        node.Target.Accept(visitor: this) || node.Value.Accept(visitor: this);
    public bool VisitBinaryExpression(BinaryExpression node) =>
        node.Left.Accept(visitor: this) || node.Right.Accept(visitor: this);
    public bool VisitUnaryExpression(UnaryExpression node) => node.Operand.Accept(visitor: this);
    public bool VisitCallExpression(CallExpression node) =>
        node.Callee.Accept(visitor: this) || Any(xs: node.Arguments);
    public bool VisitNamedArgumentExpression(NamedArgumentExpression node) =>
        node.Value.Accept(visitor: this);
    public bool VisitCreatorExpression(CreatorExpression node) =>
        node.MemberVariables.Any(predicate: mv => mv.Value.Accept(visitor: this));
    public bool VisitWithExpression(WithExpression node) =>
        node.Base.Accept(visitor: this) ||
        node.Updates.Any(predicate: u =>
            u.Value.Accept(visitor: this) ||
            (u.Index?.Accept(visitor: this) ?? false));
    public bool VisitMemberExpression(MemberExpression node) => node.Object.Accept(visitor: this);
    public bool VisitSpliceExpression(SpliceExpression node) => node.Inner.Accept(visitor: this);
    public bool VisitSpliceMemberExpression(SpliceMemberExpression node) =>
        node.Object.Accept(visitor: this);
    public bool VisitOptionalMemberExpression(OptionalMemberExpression node) =>
        node.Object.Accept(visitor: this);
    public bool VisitIndexExpression(IndexExpression node) =>
        node.Object.Accept(visitor: this) || node.Index.Accept(visitor: this);
    public bool VisitConditionalExpression(ConditionalExpression node) =>
        node.Condition.Accept(visitor: this) ||
        node.TrueExpression.Accept(visitor: this) ||
        node.FalseExpression.Accept(visitor: this);
    public bool VisitBlockExpression(BlockExpression node) => node.Value.Accept(visitor: this);
    public bool VisitChainedComparisonExpression(ChainedComparisonExpression node) =>
        Any(xs: node.Operands);
    public bool VisitRangeExpression(RangeExpression node) =>
        node.Start.Accept(visitor: this) ||
        node.End.Accept(visitor: this) ||
        (node.Step?.Accept(visitor: this) ?? false);
    public bool VisitLambdaExpression(LambdaExpression node) => node.Body.Accept(visitor: this);
    public bool VisitTypeConversionExpression(TypeConversionExpression node) =>
        node.Expression.Accept(visitor: this);
    public bool VisitGenericMemberRoutineCallExpression(GenericMemberRoutineCallExpression node) =>
        node.Object.Accept(visitor: this) || Any(xs: node.Arguments);
    public bool VisitGenericMemberExpression(GenericMemberExpression node) =>
        node.Object.Accept(visitor: this);
    public bool VisitBracketAccessExpression(BracketAccessExpression node) =>
        throw new System.InvalidOperationException(
            "BracketAccessExpression must be lowered by BracketReclassifyPass before analysis.");
    public bool VisitCarrierPayloadExpression(CarrierPayloadExpression node) =>
        node.Carrier.Accept(visitor: this);
    public bool VisitCrashableDispatchExpression(CrashableDispatchExpression node) =>
        node.Carrier.Accept(visitor: this);
    public bool VisitIsPatternExpression(IsPatternExpression node) =>
        node.Expression.Accept(visitor: this) || ScanPattern(pattern: node.Pattern);
    public bool VisitFlagsTestExpression(FlagsTestExpression node) =>
        node.Subject.Accept(visitor: this);
    public bool VisitWhenExpression(WhenExpression node) =>
        (node.Expression?.Accept(visitor: this) ?? false) ||
        node.Clauses.Any(predicate: c =>
            ScanPattern(pattern: c.Pattern) || c.Body.Accept(visitor: this));
    public bool VisitStealExpression(StealExpression node) => node.Operand.Accept(visitor: this);
    public bool VisitWaitforExpression(WaitforExpression node) =>
        node.Operand.Accept(visitor: this) ||
        (node.Timeout?.Accept(visitor: this) ?? false);
    public bool VisitDependentWaitforExpression(DependentWaitforExpression node) =>
        node.Operand.Accept(visitor: this) ||
        (node.Timeout?.Accept(visitor: this) ?? false);
    public bool VisitBackIndexExpression(BackIndexExpression node) =>
        node.Operand.Accept(visitor: this);
    public bool VisitInsertedTextExpression(InsertedTextExpression node) =>
        node.Parts.OfType<ExpressionPart>()
            .Any(predicate: p => p.Expression.Accept(visitor: this));

    public bool VisitExpressionStatement(ExpressionStatement node) =>
        node.Expression.Accept(visitor: this);
    public bool VisitDeclarationStatement(DeclarationStatement node) =>
        node.Declaration is VariableDeclaration { Initializer: not null } v &&
        v.Initializer.Accept(visitor: this);
    public bool VisitAssignmentStatement(AssignmentStatement node) =>
        node.Target.Accept(visitor: this) || node.Value.Accept(visitor: this);
    public bool VisitDestructuringStatement(DestructuringStatement node) =>
        node.Initializer.Accept(visitor: this);
    public bool VisitReturnStatement(ReturnStatement node) =>
        node.Value?.Accept(visitor: this) ?? false;
    public bool VisitBecomesStatement(BecomesStatement node) => node.Value.Accept(visitor: this);
    public bool VisitVariantReturnStatement(VariantReturnStatement node) =>
        node.Value?.Accept(visitor: this) ?? false;
    public bool VisitThrowStatement(ThrowStatement node) => node.Error.Accept(visitor: this);
    public bool VisitIfStatement(IfStatement node) =>
        node.Condition.Accept(visitor: this) ||
        node.ThenStatement.Accept(visitor: this) ||
        (node.ElseStatement?.Accept(visitor: this) ?? false);
    public bool VisitWhileStatement(WhileStatement node) =>
        node.Condition.Accept(visitor: this) ||
        node.Body.Accept(visitor: this) ||
        (node.ElseBranch?.Accept(visitor: this) ?? false);
    public bool VisitLoopStatement(LoopStatement node) => node.Body.Accept(visitor: this);
    public bool VisitExpandStatement(ExpandStatement node) => node.Body.Accept(visitor: this);
    public bool VisitEachStatement(EachStatement node) =>
        node.Iterable.Accept(visitor: this) ||
        node.Body.Accept(visitor: this) ||
        (node.ElseBranch?.Accept(visitor: this) ?? false);
    public bool VisitBlockStatement(BlockStatement node) => AnyStmt(xs: node.Statements);
    public bool VisitWhenStatement(WhenStatement node) =>
        node.Expression.Accept(visitor: this) ||
        node.Clauses.Any(predicate: c =>
            ScanPattern(pattern: c.Pattern) || c.Body.Accept(visitor: this)) ||
        (node.ArmExpansion?.Template.Body.Accept(visitor: this) ?? false);
    public bool VisitDangerStatement(DangerStatement node) => node.Body.Accept(visitor: this);
    public bool VisitUsingStatement(UsingStatement node) =>
        node.Resource.Accept(visitor: this) || node.Body.Accept(visitor: this) ||
        node.FallbackBody?.Accept(visitor: this) == true;
    public bool VisitDiscardStatement(DiscardStatement node) =>
        node.Expression.Accept(visitor: this);

    public bool VisitVariableDeclaration(VariableDeclaration node) =>
        node.Initializer?.Accept(visitor: this) ?? false;

    // Decl-position expand carries only member-variable templates (no runtime `me` refs).
    public bool VisitExpandMemberDeclaration(ExpandMemberDeclaration node) => false;

    // Nested declarations within a routine body do not capture the outer `me` —
    // any `me` reference inside them belongs to a different owner. Treat as no-hit.
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

    private bool ScanPattern(Pattern? pattern) => pattern switch
    {
        ExpressionPattern e => e.Expression.Accept(visitor: this),
        ComparisonPattern c => c.Value.Accept(visitor: this),
        GuardPattern g => ScanPattern(pattern: g.InnerPattern) || g.Guard.Accept(visitor: this),
        _ => false
    };
}