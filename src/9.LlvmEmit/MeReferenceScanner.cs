using SyntaxTree;

namespace Compiler.LlvmEmit;

/// <summary>
/// Returns <c>true</c> on the first descendant that is an
/// <see cref="IdentifierExpression"/> with <c>Name == "me"</c>.
/// Used to decide whether an entity <c>create</c> routine needs an implicit
/// <c>me</c> allocation at routine entry — canonical <c>return Type(field: …)</c>
/// factories that never touch <c>me</c> skip the extra heap allocation.
/// </summary>
internal sealed class MeReferenceScanner : ISyntaxTreeVisitor<bool>
{
    public static bool Scan(Statement body)
    {
        return body.Accept(visitor: new MeReferenceScanner());
    }

    private bool Any(IEnumerable<Expression>? xs)
    {
        return xs != null && xs.Any(predicate: x => x.Accept(visitor: this));
    }

    private bool AnyStmt(IEnumerable<Statement>? xs)
    {
        return xs != null && xs.Any(predicate: x => x.Accept(visitor: this));
    }

    public bool VisitIdentifierExpression(IdentifierExpression node)
    {
        return node.Name == "me";
    }

    public bool VisitLiteralExpression(LiteralExpression node)
    {
        return false;
    }
    public bool VisitTypeExpression(TypeExpression node)
    {
        return false;
    }
    public bool VisitTypeIdExpression(TypeIdExpression node)
    {
        return false;
    }
    public bool VisitBreakStatement(BreakStatement node)
    {
        return false;
    }
    public bool VisitContinueStatement(ContinueStatement node)
    {
        return false;
    }
    public bool VisitAbsentStatement(AbsentStatement node)
    {
        return false;
    }
    public bool VisitPassStatement(PassStatement node)
    {
        return false;
    }

    public bool VisitListLiteralExpression(ListLiteralExpression node)
    {
        return Any(xs: node.Elements);
    }
    public bool VisitSetLiteralExpression(SetLiteralExpression node)
    {
        return Any(xs: node.Elements);
    }
    public bool VisitTupleLiteralExpression(TupleLiteralExpression node)
    {
        return Any(xs: node.Elements);
    }
    public bool VisitDictLiteralExpression(DictLiteralExpression node)
    {
        return node.Pairs.Any(predicate: e =>
            e.Key.Accept(visitor: this) || e.Value.Accept(visitor: this));
    }
    public bool VisitDictEntryLiteralExpression(DictEntryLiteralExpression node)
    {
        return node.Key.Accept(visitor: this) || node.Value.Accept(visitor: this);
    }

    public bool VisitCompoundAssignmentExpression(CompoundAssignmentExpression node)
    {
        return node.Target.Accept(visitor: this) || node.Value.Accept(visitor: this);
    }
    public bool VisitBinaryExpression(BinaryExpression node)
    {
        return node.Left.Accept(visitor: this) || node.Right.Accept(visitor: this);
    }
    public bool VisitUnaryExpression(UnaryExpression node)
    {
        return node.Operand.Accept(visitor: this);
    }
    public bool VisitCallExpression(CallExpression node)
    {
        return node.Callee.Accept(visitor: this) || Any(xs: node.Arguments);
    }
    public bool VisitNamedArgumentExpression(NamedArgumentExpression node)
    {
        return node.Value.Accept(visitor: this);
    }
    public bool VisitCreatorExpression(CreatorExpression node)
    {
        return node.MemberVariables.Any(predicate: mv => mv.Value.Accept(visitor: this));
    }
    public bool VisitWithExpression(WithExpression node)
    {
        return node.Base.Accept(visitor: this) || node.Updates.Any(predicate: u =>
            u.Value.Accept(visitor: this) || (u.Index?.Accept(visitor: this) ?? false));
    }
    public bool VisitMemberExpression(MemberExpression node)
    {
        return node.Object.Accept(visitor: this);
    }
    public bool VisitSpliceExpression(SpliceExpression node)
    {
        return node.Inner.Accept(visitor: this);
    }
    public bool VisitSpliceMemberExpression(SpliceMemberExpression node)
    {
        return node.Object.Accept(visitor: this);
    }
    public bool VisitOptionalMemberExpression(OptionalMemberExpression node)
    {
        return node.Object.Accept(visitor: this);
    }
    public bool VisitIndexExpression(IndexExpression node)
    {
        return node.Object.Accept(visitor: this) || node.Index.Accept(visitor: this);
    }
    public bool VisitConditionalExpression(ConditionalExpression node)
    {
        return node.Condition.Accept(visitor: this) || node.TrueExpression.Accept(visitor: this) ||
               node.FalseExpression.Accept(visitor: this);
    }
    public bool VisitBlockExpression(BlockExpression node)
    {
        return node.Value.Accept(visitor: this);
    }
    public bool VisitChainedComparisonExpression(ChainedComparisonExpression node)
    {
        return Any(xs: node.Operands);
    }
    public bool VisitRangeExpression(RangeExpression node)
    {
        return node.Start.Accept(visitor: this) || node.End.Accept(visitor: this) ||
               (node.Step?.Accept(visitor: this) ?? false);
    }
    public bool VisitLambdaExpression(LambdaExpression node)
    {
        return node.Body.Accept(visitor: this);
    }
    public bool VisitTypeConversionExpression(TypeConversionExpression node)
    {
        return node.Expression.Accept(visitor: this);
    }
    public bool VisitGenericMemberRoutineCallExpression(GenericMemberRoutineCallExpression node)
    {
        return node.Object.Accept(visitor: this) || Any(xs: node.Arguments);
    }
    public bool VisitGenericMemberExpression(GenericMemberExpression node)
    {
        return node.Object.Accept(visitor: this);
    }
    public bool VisitBracketAccessExpression(BracketAccessExpression node)
    {
        throw new InvalidOperationException(
            message:
            "BracketAccessExpression must be lowered by BracketReclassifyPass before analysis.");
    }
    public bool VisitCarrierPayloadExpression(CarrierPayloadExpression node)
    {
        return node.Carrier.Accept(visitor: this);
    }
    public bool VisitCrashableDispatchExpression(CrashableDispatchExpression node)
    {
        return node.Carrier.Accept(visitor: this);
    }
    public bool VisitIsPatternExpression(IsPatternExpression node)
    {
        return node.Expression.Accept(visitor: this) || ScanPattern(pattern: node.Pattern);
    }
    public bool VisitFlagsTestExpression(FlagsTestExpression node)
    {
        return node.Subject.Accept(visitor: this);
    }
    public bool VisitWhenExpression(WhenExpression node)
    {
        return (node.Expression?.Accept(visitor: this) ?? false) || node.Clauses.Any(
            predicate: c => ScanPattern(pattern: c.Pattern) || c.Body.Accept(visitor: this));
    }
    public bool VisitStealExpression(StealExpression node)
    {
        return node.Operand.Accept(visitor: this);
    }
    public bool VisitWaitforExpression(WaitforExpression node)
    {
        return node.Operand.Accept(visitor: this) ||
               (node.Timeout?.Accept(visitor: this) ?? false);
    }
    public bool VisitDependentWaitforExpression(DependentWaitforExpression node)
    {
        return node.Operand.Accept(visitor: this) ||
               (node.Timeout?.Accept(visitor: this) ?? false);
    }
    public bool VisitBackIndexExpression(BackIndexExpression node)
    {
        return node.Operand.Accept(visitor: this);
    }
    public bool VisitInsertedTextExpression(InsertedTextExpression node)
    {
        return node.Parts
                   .OfType<ExpressionPart>()
                   .Any(predicate: p => p.Expression.Accept(visitor: this));
    }

    public bool VisitExpressionStatement(ExpressionStatement node)
    {
        return node.Expression.Accept(visitor: this);
    }
    public bool VisitDeclarationStatement(DeclarationStatement node)
    {
        return node.Declaration is VariableDeclaration { Initializer: not null } v &&
               v.Initializer.Accept(visitor: this);
    }
    public bool VisitAssignmentStatement(AssignmentStatement node)
    {
        return node.Target.Accept(visitor: this) || node.Value.Accept(visitor: this);
    }
    public bool VisitDestructuringStatement(DestructuringStatement node)
    {
        return node.Initializer.Accept(visitor: this);
    }
    public bool VisitReturnStatement(ReturnStatement node)
    {
        return node.Value?.Accept(visitor: this) ?? false;
    }
    public bool VisitBecomesStatement(BecomesStatement node)
    {
        return node.Value.Accept(visitor: this);
    }
    public bool VisitVariantReturnStatement(VariantReturnStatement node)
    {
        return node.Value?.Accept(visitor: this) ?? false;
    }
    public bool VisitThrowStatement(ThrowStatement node)
    {
        return node.Error.Accept(visitor: this);
    }
    public bool VisitIfStatement(IfStatement node)
    {
        return node.Condition.Accept(visitor: this) || node.ThenStatement.Accept(visitor: this) ||
               (node.ElseStatement?.Accept(visitor: this) ?? false);
    }
    public bool VisitWhileStatement(WhileStatement node)
    {
        return node.Condition.Accept(visitor: this) || node.Body.Accept(visitor: this) ||
               (node.ElseBranch?.Accept(visitor: this) ?? false);
    }
    public bool VisitLoopStatement(LoopStatement node)
    {
        return node.Body.Accept(visitor: this);
    }
    public bool VisitExpandStatement(ExpandStatement node)
    {
        return node.Body.Accept(visitor: this);
    }
    public bool VisitEachStatement(EachStatement node)
    {
        return node.Iterable.Accept(visitor: this) || node.Body.Accept(visitor: this) ||
               (node.ElseBranch?.Accept(visitor: this) ?? false);
    }
    public bool VisitBlockStatement(BlockStatement node)
    {
        return AnyStmt(xs: node.Statements);
    }
    public bool VisitWhenStatement(WhenStatement node)
    {
        return node.Expression.Accept(visitor: this) ||
               node.Clauses.Any(predicate: c =>
                   ScanPattern(pattern: c.Pattern) || c.Body.Accept(visitor: this)) ||
               (node.ArmExpansion?.Template.Body.Accept(visitor: this) ?? false);
    }
    public bool VisitDangerStatement(DangerStatement node)
    {
        return node.Body.Accept(visitor: this);
    }
    public bool VisitUsingStatement(UsingStatement node)
    {
        return node.Resource.Accept(visitor: this) || node.Body.Accept(visitor: this) ||
               node.FallbackBody?.Accept(visitor: this) == true;
    }
    public bool VisitDiscardStatement(DiscardStatement node)
    {
        return node.Expression.Accept(visitor: this);
    }

    public bool VisitVariableDeclaration(VariableDeclaration node)
    {
        return node.Initializer?.Accept(visitor: this) ?? false;
    }

    // Decl-position expand carries only member-variable templates (no runtime `me` refs).
    public bool VisitExpandMemberDeclaration(ExpandMemberDeclaration node)
    {
        return false;
    }

    // Nested declarations within a routine body do not capture the outer `me` —
    // any `me` reference inside them belongs to a different owner. Treat as no-hit.
    public bool VisitFunctionDeclaration(RoutineDeclaration node)
    {
        return false;
    }
    public bool VisitEntityDeclaration(EntityDeclaration node)
    {
        return false;
    }
    public bool VisitRecordDeclaration(RecordDeclaration node)
    {
        return false;
    }
    public bool VisitChoiceDeclaration(ChoiceDeclaration node)
    {
        return false;
    }
    public bool VisitFlagsDeclaration(FlagsDeclaration node)
    {
        return false;
    }
    public bool VisitCrashableDeclaration(CrashableDeclaration node)
    {
        return false;
    }
    public bool VisitVariantDeclaration(VariantDeclaration node)
    {
        return false;
    }
    public bool VisitProtocolDeclaration(ProtocolDeclaration node)
    {
        return false;
    }
    public bool VisitImportDeclaration(ImportDeclaration node)
    {
        return false;
    }
    public bool VisitModuleDeclaration(ModuleDeclaration node)
    {
        return false;
    }
    public bool VisitDefineDeclaration(DefineDeclaration node)
    {
        return false;
    }
    public bool VisitExternalDeclaration(ExternalDeclaration node)
    {
        return false;
    }
    public bool VisitExternalBlockDeclaration(ExternalBlockDeclaration node)
    {
        return false;
    }
    public bool VisitPresetDeclaration(PresetDeclaration node)
    {
        return false;
    }
    public bool VisitProgram(Program node)
    {
        return false;
    }

    private bool ScanPattern(Pattern? pattern)
    {
        return pattern switch
        {
            ExpressionPattern e => e.Expression.Accept(visitor: this),
            ComparisonPattern c => c.Value.Accept(visitor: this),
            GuardPattern g => ScanPattern(pattern: g.InnerPattern) ||
                              g.Guard.Accept(visitor: this),
            _ => false
        };
    }
}
