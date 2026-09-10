using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Compiler.Lowering.Passes;

/// <summary>
/// Phase 8 pass: lift lambdas into synthesized top-level routines after verification is complete.
/// Non-capturing lambdas are lifted as-is. Capturing lambdas (explicit <c>given</c> clause) are
/// supported only in the immediately-invoked form — captures become leading parameters and the
/// call site is expanded inline. Escaping capturing lambdas (stored in variables or passed as
/// arguments) still require full closure lowering and are rejected here.
/// </summary>
internal sealed class LambdaLiftingPass(PostprocessingContext ctx)
{
    private int _lambdaCounter;
    private readonly List<RoutineDeclaration> _liftedRoutines = [];
    private string? _currentModuleName;

    public void Run(Program program)
    {
        _currentModuleName = program.Declarations
                                    .OfType<ModuleDeclaration>()
                                    .LastOrDefault()
                                   ?.Path;
        _liftedRoutines.Clear();

        for (int i = 0; i < program.Declarations.Count; i++)
        {
            switch (program.Declarations[index: i])
            {
                case RoutineDeclaration routine:
                    program.Declarations[index: i] = RewriteRoutine(routine: routine,
                        scope: BuildRoutineScope(routine: routine, includeMe: false),
                        inheritedGenericParameters: routine.GenericParameters,
                        inheritedGenericConstraints: routine.GenericConstraints,
                        includeMe: false);
                    break;

                case EntityDeclaration entity:
                    RewriteMemberList(members: entity.Members,
                        ownerGenericParameters: entity.GenericParameters,
                        ownerGenericConstraints: entity.GenericConstraints);
                    break;

                case RecordDeclaration record:
                    RewriteMemberList(members: record.Members,
                        ownerGenericParameters: record.GenericParameters,
                        ownerGenericConstraints: record.GenericConstraints);
                    break;

                case CrashableDeclaration crashable:
                    RewriteMemberList(members: crashable.Members,
                        ownerGenericParameters: null,
                        ownerGenericConstraints: null);
                    break;
            }
        }

        foreach (RoutineDeclaration lifted in _liftedRoutines)
        {
            program.Declarations.Add(item: lifted);
        }
    }

    private void RewriteMemberList(List<SyntaxTree.Declaration> members,
        List<string>? ownerGenericParameters,
        List<GenericConstraintDeclaration>? ownerGenericConstraints)
    {
        for (int i = 0; i < members.Count; i++)
        {
            if (members[index: i] is not RoutineDeclaration routine)
            {
                continue;
            }

            members[index: i] = RewriteRoutine(routine: routine,
                scope: BuildRoutineScope(routine: routine, includeMe: true),
                inheritedGenericParameters: MergeGenericParameters(a: ownerGenericParameters,
                    b: routine.GenericParameters),
                inheritedGenericConstraints: MergeGenericConstraints(a: ownerGenericConstraints,
                    b: routine.GenericConstraints),
                includeMe: true);
        }
    }

    private RoutineDeclaration RewriteRoutine(RoutineDeclaration routine, HashSet<string> scope,
        List<string>? inheritedGenericParameters,
        List<GenericConstraintDeclaration>? inheritedGenericConstraints, bool includeMe)
    {
        Statement body = RewriteStatement(statement: routine.Body,
            scope: scope,
            inheritedGenericParameters: inheritedGenericParameters,
            inheritedGenericConstraints: inheritedGenericConstraints,
            includeMe: includeMe);

        return ReferenceEquals(objA: body, objB: routine.Body)
            ? routine
            : routine with { Body = body };
    }

    private Statement RewriteStatement(Statement statement, HashSet<string> scope,
        List<string>? inheritedGenericParameters,
        List<GenericConstraintDeclaration>? inheritedGenericConstraints, bool includeMe)
    {
        return statement switch
        {
            BlockStatement block => RewriteBlock(block: block,
                scope: scope,
                inheritedGenericParameters: inheritedGenericParameters,
                inheritedGenericConstraints: inheritedGenericConstraints,
                includeMe: includeMe),
            IfStatement ifs => ifs with
            {
                Condition =
                RewriteExpression(expression: ifs.Condition,
                    scope: scope,
                    inheritedGenericParameters: inheritedGenericParameters,
                    inheritedGenericConstraints: inheritedGenericConstraints,
                    includeMe: includeMe),
                ThenStatement = RewriteStatement(statement: ifs.ThenStatement,
                    scope:
                    new HashSet<string>(collection: scope, comparer: StringComparer.Ordinal),
                    inheritedGenericParameters: inheritedGenericParameters,
                    inheritedGenericConstraints: inheritedGenericConstraints,
                    includeMe: includeMe),
                ElseStatement = ifs.ElseStatement != null
                    ? RewriteStatement(statement: ifs.ElseStatement,
                        scope: new HashSet<string>(collection: scope,
                            comparer: StringComparer.Ordinal),
                        inheritedGenericParameters: inheritedGenericParameters,
                        inheritedGenericConstraints: inheritedGenericConstraints,
                        includeMe: includeMe)
                    : null
            },
            WhileStatement whileStmt => whileStmt with
            {
                Condition =
                RewriteExpression(expression: whileStmt.Condition,
                    scope: scope,
                    inheritedGenericParameters: inheritedGenericParameters,
                    inheritedGenericConstraints: inheritedGenericConstraints,
                    includeMe: includeMe),
                Body = RewriteStatement(statement: whileStmt.Body,
                    scope:
                    new HashSet<string>(collection: scope, comparer: StringComparer.Ordinal),
                    inheritedGenericParameters: inheritedGenericParameters,
                    inheritedGenericConstraints: inheritedGenericConstraints,
                    includeMe: includeMe),
                ElseBranch = whileStmt.ElseBranch != null
                    ? RewriteStatement(statement: whileStmt.ElseBranch,
                        scope: new HashSet<string>(collection: scope,
                            comparer: StringComparer.Ordinal),
                        inheritedGenericParameters: inheritedGenericParameters,
                        inheritedGenericConstraints: inheritedGenericConstraints,
                        includeMe: includeMe)
                    : null
            },
            LoopStatement loop => loop with
            {
                Body = RewriteStatement(statement: loop.Body,
                    scope: new HashSet<string>(collection: scope,
                        comparer: StringComparer.Ordinal),
                    inheritedGenericParameters: inheritedGenericParameters,
                    inheritedGenericConstraints: inheritedGenericConstraints,
                    includeMe: includeMe)
            },
            EachStatement eachStmt => RewriteEach(eachStmt: eachStmt,
                scope: scope,
                inheritedGenericParameters: inheritedGenericParameters,
                inheritedGenericConstraints: inheritedGenericConstraints,
                includeMe: includeMe),
            WhenStatement whenStmt => RewriteWhen(whenStmt: whenStmt,
                scope: scope,
                inheritedGenericParameters: inheritedGenericParameters,
                inheritedGenericConstraints: inheritedGenericConstraints,
                includeMe: includeMe),
            UsingStatement usingStmt => usingStmt with
            {
                Resource = RewriteExpression(expression: usingStmt.Resource,
                    scope: scope,
                    inheritedGenericParameters: inheritedGenericParameters,
                    inheritedGenericConstraints: inheritedGenericConstraints,
                    includeMe: includeMe),
                Body = RewriteStatement(statement: usingStmt.Body,
                    scope: [.. scope, usingStmt.Name],
                    inheritedGenericParameters: inheritedGenericParameters,
                    inheritedGenericConstraints: inheritedGenericConstraints,
                    includeMe: includeMe),
                // The fallback branch runs when acquisition fails — the bound name is NOT in scope.
                FallbackBody = usingStmt.FallbackBody != null
                    ? RewriteStatement(statement: usingStmt.FallbackBody,
                        scope: scope,
                        inheritedGenericParameters: inheritedGenericParameters,
                        inheritedGenericConstraints: inheritedGenericConstraints,
                        includeMe: includeMe)
                    : null
            },
            DangerStatement danger => danger with
            {
                Body = (BlockStatement)RewriteStatement(statement: danger.Body,
                    scope: new HashSet<string>(collection: scope,
                        comparer: StringComparer.Ordinal),
                    inheritedGenericParameters: inheritedGenericParameters,
                    inheritedGenericConstraints: inheritedGenericConstraints,
                    includeMe: includeMe)
            },
            DeclarationStatement { Declaration: VariableDeclaration variable } decl => decl with
            {
                Declaration = variable.Initializer != null
                    ? variable with
                    {
                        Initializer = RewriteExpression(expression: variable.Initializer,
                            scope: scope,
                            inheritedGenericParameters: inheritedGenericParameters,
                            inheritedGenericConstraints: inheritedGenericConstraints,
                            includeMe: includeMe)
                    }
                    : variable
            },
            AssignmentStatement assignment => assignment with
            {
                Target = RewriteExpression(expression: assignment.Target,
                    scope: scope,
                    inheritedGenericParameters: inheritedGenericParameters,
                    inheritedGenericConstraints: inheritedGenericConstraints,
                    includeMe: includeMe),
                Value = RewriteExpression(expression: assignment.Value,
                    scope: scope,
                    inheritedGenericParameters: inheritedGenericParameters,
                    inheritedGenericConstraints: inheritedGenericConstraints,
                    includeMe: includeMe)
            },
            ReturnStatement { Value: not null } ret => ret with
            {
                Value = RewriteExpression(expression: ret.Value,
                    scope: scope,
                    inheritedGenericParameters: inheritedGenericParameters,
                    inheritedGenericConstraints: inheritedGenericConstraints,
                    includeMe: includeMe)
            },
            ExpressionStatement exprStmt => exprStmt with
            {
                Expression = RewriteExpression(expression: exprStmt.Expression,
                    scope: scope,
                    inheritedGenericParameters: inheritedGenericParameters,
                    inheritedGenericConstraints: inheritedGenericConstraints,
                    includeMe: includeMe)
            },
            DiscardStatement discard => discard with
            {
                Expression = RewriteExpression(expression: discard.Expression,
                    scope: scope,
                    inheritedGenericParameters: inheritedGenericParameters,
                    inheritedGenericConstraints: inheritedGenericConstraints,
                    includeMe: includeMe)
            },
            BecomesStatement becomes => becomes with
            {
                Value = RewriteExpression(expression: becomes.Value,
                    scope: scope,
                    inheritedGenericParameters: inheritedGenericParameters,
                    inheritedGenericConstraints: inheritedGenericConstraints,
                    includeMe: includeMe)
            },
            ThrowStatement throwStmt => throwStmt with
            {
                Error = RewriteExpression(expression: throwStmt.Error,
                    scope: scope,
                    inheritedGenericParameters: inheritedGenericParameters,
                    inheritedGenericConstraints: inheritedGenericConstraints,
                    includeMe: includeMe)
            },
            VariantReturnStatement { Value: not null } variantReturn => variantReturn with
            {
                Value = RewriteExpression(expression: variantReturn.Value,
                    scope: scope,
                    inheritedGenericParameters: inheritedGenericParameters,
                    inheritedGenericConstraints: inheritedGenericConstraints,
                    includeMe: includeMe)
            },
            DestructuringStatement destructuring => destructuring with
            {
                Initializer = RewriteExpression(expression: destructuring.Initializer,
                    scope: scope,
                    inheritedGenericParameters: inheritedGenericParameters,
                    inheritedGenericConstraints: inheritedGenericConstraints,
                    includeMe: includeMe)
            },
            _ => statement
        };
    }

    private Statement RewriteBlock(BlockStatement block, HashSet<string> scope,
        List<string>? inheritedGenericParameters,
        List<GenericConstraintDeclaration>? inheritedGenericConstraints, bool includeMe)
    {
        var blockScope = new HashSet<string>(collection: scope, comparer: StringComparer.Ordinal);
        var statements = new List<Statement>(capacity: block.Statements.Count);

        foreach (Statement statement in block.Statements)
        {
            Statement rewritten = RewriteStatement(statement: statement,
                scope: blockScope,
                inheritedGenericParameters: inheritedGenericParameters,
                inheritedGenericConstraints: inheritedGenericConstraints,
                includeMe: includeMe);
            statements.Add(item: rewritten);
            AddStatementBindings(scope: blockScope, statement: rewritten);
        }

        return block with { Statements = statements };
    }

    private Statement RewriteEach(EachStatement eachStmt, HashSet<string> scope,
        List<string>? inheritedGenericParameters,
        List<GenericConstraintDeclaration>? inheritedGenericConstraints, bool includeMe)
    {
        var bodyScope = new HashSet<string>(collection: scope, comparer: StringComparer.Ordinal);
        if (eachStmt.Variable != null)
        {
            bodyScope.Add(item: eachStmt.Variable);
        }

        foreach (string binding in GetPatternBindings(pattern: eachStmt.VariablePattern))
        {
            bodyScope.Add(item: binding);
        }

        return eachStmt with
        {
            Iterable =
            RewriteExpression(expression: eachStmt.Iterable,
                scope: scope,
                inheritedGenericParameters: inheritedGenericParameters,
                inheritedGenericConstraints: inheritedGenericConstraints,
                includeMe: includeMe),
            Body = RewriteStatement(statement: eachStmt.Body,
                scope: bodyScope,
                inheritedGenericParameters: inheritedGenericParameters,
                inheritedGenericConstraints: inheritedGenericConstraints,
                includeMe: includeMe),
            ElseBranch = eachStmt.ElseBranch != null
                ? RewriteStatement(statement: eachStmt.ElseBranch,
                    scope:
                    new HashSet<string>(collection: scope, comparer: StringComparer.Ordinal),
                    inheritedGenericParameters: inheritedGenericParameters,
                    inheritedGenericConstraints: inheritedGenericConstraints,
                    includeMe: includeMe)
                : null
        };
    }

    private Statement RewriteWhen(WhenStatement whenStmt, HashSet<string> scope,
        List<string>? inheritedGenericParameters,
        List<GenericConstraintDeclaration>? inheritedGenericConstraints, bool includeMe)
    {
        var clauses = new List<WhenClause>(capacity: whenStmt.Clauses.Count);

        foreach (WhenClause clause in whenStmt.Clauses)
        {
            var clauseScope =
                new HashSet<string>(collection: scope, comparer: StringComparer.Ordinal);
            foreach (string binding in GetPatternBindings(pattern: clause.Pattern))
            {
                clauseScope.Add(item: binding);
            }

            clauses.Add(item: clause with
            {
                Pattern = RewritePatternExpressions(pattern: clause.Pattern,
                    scope: scope,
                    inheritedGenericParameters: inheritedGenericParameters,
                    inheritedGenericConstraints: inheritedGenericConstraints,
                    includeMe: includeMe),
                Body = RewriteStatement(statement: clause.Body,
                    scope: clauseScope,
                    inheritedGenericParameters: inheritedGenericParameters,
                    inheritedGenericConstraints: inheritedGenericConstraints,
                    includeMe: includeMe)
            });
        }

        return whenStmt with
        {
            Expression = RewriteExpression(expression: whenStmt.Expression,
                scope: scope,
                inheritedGenericParameters: inheritedGenericParameters,
                inheritedGenericConstraints: inheritedGenericConstraints,
                includeMe: includeMe),
            Clauses = clauses
        };
    }

    private Expression RewriteExpression(Expression expression, HashSet<string> scope,
        List<string>? inheritedGenericParameters,
        List<GenericConstraintDeclaration>? inheritedGenericConstraints, bool includeMe)
    {
        return expression switch
        {
            LambdaExpression lambda => LiftLambda(lambda: lambda,
                scope: scope,
                inheritedGenericParameters: inheritedGenericParameters,
                inheritedGenericConstraints: inheritedGenericConstraints,
                includeMe: includeMe),
            BinaryExpression binary => CopyResolvedType(rewritten: binary with
                {
                    Left = RewriteExpression(expression: binary.Left,
                        scope: scope,
                        inheritedGenericParameters: inheritedGenericParameters,
                        inheritedGenericConstraints: inheritedGenericConstraints,
                        includeMe: includeMe),
                    Right = RewriteExpression(expression: binary.Right,
                        scope: scope,
                        inheritedGenericParameters: inheritedGenericParameters,
                        inheritedGenericConstraints: inheritedGenericConstraints,
                        includeMe: includeMe)
                },
                original: binary),
            UnaryExpression unary => CopyResolvedType(rewritten: unary with
                {
                    Operand = RewriteExpression(expression: unary.Operand,
                        scope: scope,
                        inheritedGenericParameters: inheritedGenericParameters,
                        inheritedGenericConstraints: inheritedGenericConstraints,
                        includeMe: includeMe)
                },
                original: unary),
            CallExpression { Callee: LambdaExpression { Captures.Count: > 0 } capLambda } call =>
                LiftCapturingLambdaIife(call: call,
                    lambda: capLambda,
                    scope: scope,
                    inheritedGenericParameters: inheritedGenericParameters,
                    inheritedGenericConstraints: inheritedGenericConstraints,
                    includeMe: includeMe),
            CallExpression call => CopyResolvedType(rewritten: call with
                {
                    Callee = RewriteExpression(expression: call.Callee,
                        scope: scope,
                        inheritedGenericParameters: inheritedGenericParameters,
                        inheritedGenericConstraints: inheritedGenericConstraints,
                        includeMe: includeMe),
                    Arguments = call.Arguments
                                    .Select(selector: arg => RewriteExpression(expression: arg,
                                         scope: scope,
                                         inheritedGenericParameters: inheritedGenericParameters,
                                         inheritedGenericConstraints: inheritedGenericConstraints,
                                         includeMe: includeMe))
                                    .ToList()
                },
                original: call),
            MemberExpression member => CopyResolvedType(rewritten: member with
                {
                    Object = RewriteExpression(expression: member.Object,
                        scope: scope,
                        inheritedGenericParameters: inheritedGenericParameters,
                        inheritedGenericConstraints: inheritedGenericConstraints,
                        includeMe: includeMe)
                },
                original: member),
            OptionalMemberExpression optionalMember => CopyResolvedType(
                rewritten: optionalMember with
                {
                    Object = RewriteExpression(expression: optionalMember.Object,
                        scope: scope,
                        inheritedGenericParameters: inheritedGenericParameters,
                        inheritedGenericConstraints: inheritedGenericConstraints,
                        includeMe: includeMe)
                },
                original: optionalMember),
            IndexExpression index => CopyResolvedType(rewritten: index with
                {
                    Object = RewriteExpression(expression: index.Object,
                        scope: scope,
                        inheritedGenericParameters: inheritedGenericParameters,
                        inheritedGenericConstraints: inheritedGenericConstraints,
                        includeMe: includeMe),
                    Index = RewriteExpression(expression: index.Index,
                        scope: scope,
                        inheritedGenericParameters: inheritedGenericParameters,
                        inheritedGenericConstraints: inheritedGenericConstraints,
                        includeMe: includeMe)
                },
                original: index),
            ConditionalExpression conditional => CopyResolvedType(rewritten: conditional with
                {
                    Condition =
                    RewriteExpression(expression: conditional.Condition,
                        scope: scope,
                        inheritedGenericParameters: inheritedGenericParameters,
                        inheritedGenericConstraints: inheritedGenericConstraints,
                        includeMe: includeMe),
                    TrueExpression =
                    RewriteExpression(expression: conditional.TrueExpression,
                        scope: scope,
                        inheritedGenericParameters: inheritedGenericParameters,
                        inheritedGenericConstraints: inheritedGenericConstraints,
                        includeMe: includeMe),
                    FalseExpression = RewriteExpression(expression: conditional.FalseExpression,
                        scope: scope,
                        inheritedGenericParameters: inheritedGenericParameters,
                        inheritedGenericConstraints: inheritedGenericConstraints,
                        includeMe: includeMe)
                },
                original: conditional),
            RangeExpression range => CopyResolvedType(rewritten: range with
                {
                    Start = RewriteExpression(expression: range.Start,
                        scope: scope,
                        inheritedGenericParameters: inheritedGenericParameters,
                        inheritedGenericConstraints: inheritedGenericConstraints,
                        includeMe: includeMe),
                    End = RewriteExpression(expression: range.End,
                        scope: scope,
                        inheritedGenericParameters: inheritedGenericParameters,
                        inheritedGenericConstraints: inheritedGenericConstraints,
                        includeMe: includeMe),
                    Step = range.Step != null
                        ? RewriteExpression(expression: range.Step,
                            scope: scope,
                            inheritedGenericParameters: inheritedGenericParameters,
                            inheritedGenericConstraints: inheritedGenericConstraints,
                            includeMe: includeMe)
                        : null
                },
                original: range),
            CreatorExpression creator => CopyResolvedType(rewritten: creator with
                {
                    MemberVariables = creator.MemberVariables
                                             .Select(selector: mv => (mv.Name,
                                                  RewriteExpression(expression: mv.Value,
                                                      scope: scope,
                                                      inheritedGenericParameters:
                                                      inheritedGenericParameters,
                                                      inheritedGenericConstraints:
                                                      inheritedGenericConstraints,
                                                      includeMe: includeMe)))
                                             .ToList()
                },
                original: creator),
            WithExpression withExpr => CopyResolvedType(rewritten: withExpr with
                {
                    Base = RewriteExpression(expression: withExpr.Base,
                        scope: scope,
                        inheritedGenericParameters: inheritedGenericParameters,
                        inheritedGenericConstraints: inheritedGenericConstraints,
                        includeMe: includeMe),
                    Updates = withExpr.Updates
                                      .Select(selector: update => (update.MemberVariablePath,
                                           update.Index != null
                                               ? RewriteExpression(expression: update.Index,
                                                   scope: scope,
                                                   inheritedGenericParameters:
                                                   inheritedGenericParameters,
                                                   inheritedGenericConstraints:
                                                   inheritedGenericConstraints,
                                                   includeMe: includeMe)
                                               : null,
                                           RewriteExpression(expression: update.Value,
                                               scope: scope,
                                               inheritedGenericParameters:
                                               inheritedGenericParameters,
                                               inheritedGenericConstraints:
                                               inheritedGenericConstraints,
                                               includeMe: includeMe)))
                                      .ToList()
                },
                original: withExpr),
            GenericMemberRoutineCallExpression genericCall => CopyResolvedType(
                rewritten: genericCall with
                {
                    Object = RewriteExpression(expression: genericCall.Object,
                        scope: scope,
                        inheritedGenericParameters: inheritedGenericParameters,
                        inheritedGenericConstraints: inheritedGenericConstraints,
                        includeMe: includeMe),
                    Arguments = genericCall.Arguments
                                           .Select(selector: arg =>
                                                RewriteExpression(expression: arg,
                                                    scope: scope,
                                                    inheritedGenericParameters:
                                                    inheritedGenericParameters,
                                                    inheritedGenericConstraints:
                                                    inheritedGenericConstraints,
                                                    includeMe: includeMe))
                                           .ToList()
                },
                original: genericCall),
            GenericMemberExpression genericMember => CopyResolvedType(rewritten: genericMember with
                {
                    Object = RewriteExpression(expression: genericMember.Object,
                        scope: scope,
                        inheritedGenericParameters: inheritedGenericParameters,
                        inheritedGenericConstraints: inheritedGenericConstraints,
                        includeMe: includeMe)
                },
                original: genericMember),
            NamedArgumentExpression namedArgument => CopyResolvedType(rewritten: namedArgument with
                {
                    Value = RewriteExpression(expression: namedArgument.Value,
                        scope: scope,
                        inheritedGenericParameters: inheritedGenericParameters,
                        inheritedGenericConstraints: inheritedGenericConstraints,
                        includeMe: includeMe)
                },
                original: namedArgument),
            ListLiteralExpression list => CopyResolvedType(rewritten: list with
                {
                    Elements = list.Elements
                                   .Select(selector: element => RewriteExpression(
                                        expression: element,
                                        scope: scope,
                                        inheritedGenericParameters: inheritedGenericParameters,
                                        inheritedGenericConstraints: inheritedGenericConstraints,
                                        includeMe: includeMe))
                                   .ToList()
                },
                original: list),
            SetLiteralExpression set => CopyResolvedType(rewritten: set with
                {
                    Elements = set.Elements
                                  .Select(selector: element => RewriteExpression(
                                       expression: element,
                                       scope: scope,
                                       inheritedGenericParameters: inheritedGenericParameters,
                                       inheritedGenericConstraints: inheritedGenericConstraints,
                                       includeMe: includeMe))
                                  .ToList()
                },
                original: set),
            DictLiteralExpression dict => CopyResolvedType(rewritten: dict with
                {
                    Pairs = dict.Pairs
                                .Select(selector: pair => (
                                     RewriteExpression(expression: pair.Key,
                                         scope: scope,
                                         inheritedGenericParameters: inheritedGenericParameters,
                                         inheritedGenericConstraints: inheritedGenericConstraints,
                                         includeMe: includeMe),
                                     RewriteExpression(expression: pair.Value,
                                         scope: scope,
                                         inheritedGenericParameters: inheritedGenericParameters,
                                         inheritedGenericConstraints: inheritedGenericConstraints,
                                         includeMe: includeMe)))
                                .ToList()
                },
                original: dict),
            TupleLiteralExpression tuple => CopyResolvedType(rewritten: tuple with
                {
                    Elements = tuple.Elements
                                    .Select(selector: element => RewriteExpression(
                                         expression: element,
                                         scope: scope,
                                         inheritedGenericParameters: inheritedGenericParameters,
                                         inheritedGenericConstraints: inheritedGenericConstraints,
                                         includeMe: includeMe))
                                    .ToList()
                },
                original: tuple),
            TypeConversionExpression conversion => CopyResolvedType(rewritten: conversion with
                {
                    Expression = RewriteExpression(expression: conversion.Expression,
                        scope: scope,
                        inheritedGenericParameters: inheritedGenericParameters,
                        inheritedGenericConstraints: inheritedGenericConstraints,
                        includeMe: includeMe)
                },
                original: conversion),
            ChainedComparisonExpression chained => CopyResolvedType(rewritten: chained with
                {
                    Operands = chained.Operands
                                      .Select(selector: operand =>
                                           RewriteExpression(expression: operand,
                                               scope: scope,
                                               inheritedGenericParameters:
                                               inheritedGenericParameters,
                                               inheritedGenericConstraints:
                                               inheritedGenericConstraints,
                                               includeMe: includeMe))
                                      .ToList()
                },
                original: chained),
            BlockExpression block => CopyResolvedType(rewritten: block with
                {
                    Value = RewriteExpression(expression: block.Value,
                        scope: scope,
                        inheritedGenericParameters: inheritedGenericParameters,
                        inheritedGenericConstraints: inheritedGenericConstraints,
                        includeMe: includeMe)
                },
                original: block),
            DictEntryLiteralExpression dictEntry => CopyResolvedType(rewritten: dictEntry with
                {
                    Key = RewriteExpression(expression: dictEntry.Key,
                        scope: scope,
                        inheritedGenericParameters: inheritedGenericParameters,
                        inheritedGenericConstraints: inheritedGenericConstraints,
                        includeMe: includeMe),
                    Value = RewriteExpression(expression: dictEntry.Value,
                        scope: scope,
                        inheritedGenericParameters: inheritedGenericParameters,
                        inheritedGenericConstraints: inheritedGenericConstraints,
                        includeMe: includeMe)
                },
                original: dictEntry),
            IsPatternExpression isPattern => CopyResolvedType(rewritten: isPattern with
                {
                    Expression = RewriteExpression(expression: isPattern.Expression,
                        scope: scope,
                        inheritedGenericParameters: inheritedGenericParameters,
                        inheritedGenericConstraints: inheritedGenericConstraints,
                        includeMe: includeMe),
                    Pattern = RewritePatternExpressions(pattern: isPattern.Pattern,
                        scope: scope,
                        inheritedGenericParameters: inheritedGenericParameters,
                        inheritedGenericConstraints: inheritedGenericConstraints,
                        includeMe: includeMe)
                },
                original: isPattern),
            FlagsTestExpression flagsTest => CopyResolvedType(rewritten: flagsTest with
                {
                    Subject = RewriteExpression(expression: flagsTest.Subject,
                        scope: scope,
                        inheritedGenericParameters: inheritedGenericParameters,
                        inheritedGenericConstraints: inheritedGenericConstraints,
                        includeMe: includeMe)
                },
                original: flagsTest),
            InsertedTextExpression inserted => CopyResolvedType(rewritten: inserted with
                {
                    Parts = inserted.Parts
                                    .Select(selector: part => part is ExpressionPart expressionPart
                                         ? expressionPart with
                                         {
                                             Expression = RewriteExpression(
                                                 expression: expressionPart.Expression,
                                                 scope: scope,
                                                 inheritedGenericParameters:
                                                 inheritedGenericParameters,
                                                 inheritedGenericConstraints:
                                                 inheritedGenericConstraints,
                                                 includeMe: includeMe)
                                         }
                                         : part)
                                    .ToList()
                },
                original: inserted),
            StealExpression steal => CopyResolvedType(rewritten: steal with
                {
                    Operand = RewriteExpression(expression: steal.Operand,
                        scope: scope,
                        inheritedGenericParameters: inheritedGenericParameters,
                        inheritedGenericConstraints: inheritedGenericConstraints,
                        includeMe: includeMe)
                },
                original: steal),
            WaitforExpression waitfor => CopyResolvedType(rewritten: waitfor with
                {
                    Operand = RewriteExpression(expression: waitfor.Operand,
                        scope: scope,
                        inheritedGenericParameters: inheritedGenericParameters,
                        inheritedGenericConstraints: inheritedGenericConstraints,
                        includeMe: includeMe),
                    Timeout = waitfor.Timeout != null
                        ? RewriteExpression(expression: waitfor.Timeout,
                            scope: scope,
                            inheritedGenericParameters: inheritedGenericParameters,
                            inheritedGenericConstraints: inheritedGenericConstraints,
                            includeMe: includeMe)
                        : null
                },
                original: waitfor),
            DependentWaitforExpression dependentWaitfor => CopyResolvedType(
                rewritten: dependentWaitfor with
                {
                    Operand = RewriteExpression(expression: dependentWaitfor.Operand,
                        scope: scope,
                        inheritedGenericParameters: inheritedGenericParameters,
                        inheritedGenericConstraints: inheritedGenericConstraints,
                        includeMe: includeMe),
                    Dependencies = dependentWaitfor.Dependencies
                                                   .Select(selector: dependency => dependency with
                                                    {
                                                        DependencyExpr = RewriteExpression(
                                                            expression:
                                                            dependency.DependencyExpr,
                                                            scope: scope,
                                                            inheritedGenericParameters:
                                                            inheritedGenericParameters,
                                                            inheritedGenericConstraints:
                                                            inheritedGenericConstraints,
                                                            includeMe: includeMe)
                                                    })
                                                   .ToList(),
                    Timeout = dependentWaitfor.Timeout != null
                        ? RewriteExpression(expression: dependentWaitfor.Timeout,
                            scope: scope,
                            inheritedGenericParameters: inheritedGenericParameters,
                            inheritedGenericConstraints: inheritedGenericConstraints,
                            includeMe: includeMe)
                        : null
                },
                original: dependentWaitfor),
            CarrierPayloadExpression payload => CopyResolvedType(rewritten: payload with
                {
                    Carrier = RewriteExpression(expression: payload.Carrier,
                        scope: scope,
                        inheritedGenericParameters: inheritedGenericParameters,
                        inheritedGenericConstraints: inheritedGenericConstraints,
                        includeMe: includeMe)
                },
                original: payload),
            BackIndexExpression backIndex => CopyResolvedType(rewritten: backIndex with
                {
                    Operand = RewriteExpression(expression: backIndex.Operand,
                        scope: scope,
                        inheritedGenericParameters: inheritedGenericParameters,
                        inheritedGenericConstraints: inheritedGenericConstraints,
                        includeMe: includeMe)
                },
                original: backIndex),
            WhenExpression whenExpr => RewriteWhenExpression(whenExpr: whenExpr,
                scope: scope,
                inheritedGenericParameters: inheritedGenericParameters,
                inheritedGenericConstraints: inheritedGenericConstraints,
                includeMe: includeMe),
            _ => expression
        };
    }

    private Expression RewriteWhenExpression(WhenExpression whenExpr, HashSet<string> scope,
        List<string>? inheritedGenericParameters,
        List<GenericConstraintDeclaration>? inheritedGenericConstraints, bool includeMe)
    {
        return CopyResolvedType(rewritten: whenExpr with
            {
                Expression = whenExpr.Expression != null
                    ? RewriteExpression(expression: whenExpr.Expression,
                        scope: scope,
                        inheritedGenericParameters: inheritedGenericParameters,
                        inheritedGenericConstraints: inheritedGenericConstraints,
                        includeMe: includeMe)
                    : null,
                Clauses = whenExpr.Clauses
                                  .Select(selector: clause => clause with
                                   {
                                       Pattern = RewritePatternExpressions(pattern: clause.Pattern,
                                           scope: scope,
                                           inheritedGenericParameters: inheritedGenericParameters,
                                           inheritedGenericConstraints:
                                           inheritedGenericConstraints,
                                           includeMe: includeMe),
                                       Body = RewriteStatement(statement: clause.Body,
                                           scope:
                                           [
                                               .. scope,
                                               .. GetPatternBindings(pattern: clause.Pattern)
                                           ],
                                           inheritedGenericParameters: inheritedGenericParameters,
                                           inheritedGenericConstraints:
                                           inheritedGenericConstraints,
                                           includeMe: includeMe)
                                   })
                                  .ToList()
            },
            original: whenExpr);
    }

    private IdentifierExpression LiftLambda(LambdaExpression lambda, HashSet<string> scope,
        List<string>? inheritedGenericParameters,
        List<GenericConstraintDeclaration>? inheritedGenericConstraints, bool includeMe)
    {
        HashSet<string> localCaptures = CollectLocalCaptures(lambda: lambda, outerScope: scope);
        // Closure conversion: every enclosing-scope variable the body references (the same set SA
        // validated against the `given` clause) travels in a heap closure. Preserve the declared
        // `given` order where present, then append any others. Their types come from the resolved
        // identifier nodes in the body.
        List<string> captureNameList = lambda.Captures != null
            ? lambda.Captures
                    .Where(predicate: localCaptures.Contains)
                    .ToList()
            : localCaptures.ToList();
        captureNameList.AddRange(
            collection: localCaptures.Where(predicate: capName =>
                !captureNameList.Contains(item: capName)));

        Dictionary<string, TypeInfo> captureTypes =
            CollectCaptureTypesFromBody(body: lambda.Body, captureNames: captureNameList);
        var closureCaptures = captureNameList.Where(predicate: captureTypes.ContainsKey)
                                             .Select(selector: n =>
                                                  (Name: n, Type: captureTypes[key: n]))
                                             .ToList();

        if (includeMe && ContainsIdentifier(expression: lambda.Body, name: "me"))
        {
            throw new InvalidOperationException(
                message: "Lambda captures 'me' and requires closure lowering before codegen.");
        }

        if (lambda.ResolvedType is not RoutineTypeInfo routineType)
        {
            throw new InvalidOperationException(
                message:
                "Lambda expression reached postprocessing without a resolved RoutineTypeInfo.");
        }

        string liftedName =
            $"__lambda_{lambda.Location.Line}_{lambda.Location.Column}_{_lambdaCounter++}";
        var genericParameters = inheritedGenericParameters?.ToList();
        var genericConstraints = inheritedGenericConstraints?.ToList();

        var lambdaScope = new HashSet<string>(comparer: StringComparer.Ordinal);
        foreach (Parameter parameter in lambda.Parameters)
        {
            lambdaScope.Add(item: parameter.Name);
        }

        Expression loweredBody = RewriteExpression(expression: lambda.Body,
            scope: lambdaScope,
            inheritedGenericParameters: genericParameters,
            inheritedGenericConstraints: genericConstraints,
            includeMe: false);

        var liftedRoutine = new RoutineDeclaration(Name: liftedName,
            Parameters: BuildLiftedParameters(lambda: lambda, routineType: routineType),
            ReturnType: routineType.ReturnType != null
                ? TypeInfoToTypeExpression(type: routineType.ReturnType, location: lambda.Location)
                : null,
            Body: new BlockStatement(Statements:
                [
                    new ReturnStatement(Value: loweredBody, Location: lambda.Location)
                ],
                Location: lambda.Location),
            Visibility: VisibilityModifier.Secret,
            Annotations: [],
            Location: lambda.Location,
            GenericParameters: genericParameters,
            GenericConstraints: genericConstraints,
            IsFailable: false,
            IsCommon: false,
            Async: AsyncStatus.None,
            IsDangerous: false);
        _liftedRoutines.Add(item: liftedRoutine);

        var liftedInfo = new RoutineInfo(name: liftedName)
        {
            Kind = RoutineKind.Lambda,
            Parameters = BuildLiftedParameterInfos(lambda: lambda, routineType: routineType),
            ReturnType = routineType.ReturnType,
            Visibility = VisibilityModifier.Secret,
            Location = lambda.Location,
            Module = _currentModuleName,
            ModulePath = _currentModuleName?.Split(separator: '/')
                                            .ToList(),
            GenericParameters = genericParameters,
            GenericConstraints = genericConstraints,
            IsSynthesized = true,
            ClosureCaptures = closureCaptures
        };
        ctx.Registry.RegisterRoutine(routine: liftedInfo);
        // Attach the RoutineInfo to the lifted declaration so the codegen-input AST dump
        // (RfAstPrinter) prints its definition — a null ResolvedInfo is otherwise dropped as an
        // "unregistered surface decl", leaving the dump showing a call to `__lambda_*` with no body.
        liftedRoutine.ResolvedInfo = liftedInfo;

        return new IdentifierExpression(Name: liftedName, Location: lambda.Location)
        {
            ResolvedType = lambda.ResolvedType
        };
    }

    private Expression LiftCapturingLambdaIife(CallExpression call, LambdaExpression lambda,
        HashSet<string> scope, List<string>? inheritedGenericParameters,
        List<GenericConstraintDeclaration>? inheritedGenericConstraints, bool includeMe)
    {
        if (lambda.ResolvedType is not RoutineTypeInfo routineType)
        {
            throw new InvalidOperationException(
                message:
                "Capturing lambda expression reached postprocessing without a resolved RoutineTypeInfo.");
        }

        List<string> captureNames = lambda.Captures!;
        Dictionary<string, TypeInfo> captureTypes =
            CollectCaptureTypesFromBody(body: lambda.Body, captureNames: captureNames);

        string liftedName =
            $"__lambda_{lambda.Location.Line}_{lambda.Location.Column}_{_lambdaCounter++}";
        var genericParameters = inheritedGenericParameters?.ToList();
        var genericConstraints = inheritedGenericConstraints?.ToList();

        // Capture params use the original capture names so the body needs no renaming.
        var captureParams = new List<Parameter>(capacity: captureNames.Count);
        var captureParamInfos = new List<ParameterInfo>(capacity: captureNames.Count);
        foreach (string captureName in captureNames)
        {
            TypeInfo captureType = captureTypes.GetValueOrDefault(key: captureName,
                defaultValue: ErrorTypeInfo.Instance);
            captureParams.Add(item: new Parameter(Name: captureName,
                Type: TypeInfoToTypeExpression(type: captureType, location: lambda.Location),
                DefaultValue: null,
                Location: lambda.Location));
            captureParamInfos.Add(item: new ParameterInfo(name: captureName, type: captureType));
        }

        // Lifted body scope: capture params + lambda params.
        var lambdaScope = new HashSet<string>(comparer: StringComparer.Ordinal);
        foreach (string captureName in captureNames)
        {
            lambdaScope.Add(item: captureName);
        }

        foreach (Parameter param in lambda.Parameters)
        {
            lambdaScope.Add(item: param.Name);
        }

        Expression loweredBody = RewriteExpression(expression: lambda.Body,
            scope: lambdaScope,
            inheritedGenericParameters: genericParameters,
            inheritedGenericConstraints: genericConstraints,
            includeMe: false);

        List<Parameter> lambdaParams =
            BuildLiftedParameters(lambda: lambda, routineType: routineType);
        List<ParameterInfo> lambdaParamInfos =
            BuildLiftedParameterInfos(lambda: lambda, routineType: routineType);

        var allParams = new List<Parameter>(capacity: captureParams.Count + lambdaParams.Count);
        allParams.AddRange(collection: captureParams);
        allParams.AddRange(collection: lambdaParams);

        var allParamInfos =
            new List<ParameterInfo>(capacity: captureParamInfos.Count + lambdaParamInfos.Count);
        allParamInfos.AddRange(collection: captureParamInfos);
        allParamInfos.AddRange(collection: lambdaParamInfos);

        var liftedRoutine = new RoutineDeclaration(Name: liftedName,
            Parameters: allParams,
            ReturnType: routineType.ReturnType != null
                ? TypeInfoToTypeExpression(type: routineType.ReturnType, location: lambda.Location)
                : null,
            Body: new BlockStatement(Statements:
                [
                    new ReturnStatement(Value: loweredBody, Location: lambda.Location)
                ],
                Location: lambda.Location),
            Visibility: VisibilityModifier.Secret,
            Annotations: [],
            Location: lambda.Location,
            GenericParameters: genericParameters,
            GenericConstraints: genericConstraints,
            IsFailable: false,
            IsCommon: false,
            Async: AsyncStatus.None,
            IsDangerous: false);
        _liftedRoutines.Add(item: liftedRoutine);

        ctx.Registry.RegisterRoutine(routine: new RoutineInfo(name: liftedName)
        {
            Kind = RoutineKind.Lambda,
            Parameters = allParamInfos,
            ReturnType = routineType.ReturnType,
            Visibility = VisibilityModifier.Secret,
            Location = lambda.Location,
            Module = _currentModuleName,
            ModulePath = _currentModuleName?.Split(separator: '/')
                                            .ToList(),
            GenericParameters = genericParameters,
            GenericConstraints = genericConstraints,
            IsSynthesized = true
        });

        // Inject capture args (named, using original capture names) before the original call args.
        var callArgs = new List<Expression>(capacity: captureNames.Count + call.Arguments.Count);
        foreach (string captureName in captureNames)
        {
            captureTypes.TryGetValue(key: captureName, value: out TypeInfo? capType);
            callArgs.Add(item: new NamedArgumentExpression(Name: captureName,
                Value: new IdentifierExpression(Name: captureName, Location: lambda.Location)
                {
                    ResolvedType = capType
                },
                Location: lambda.Location));
        }

        foreach (Expression arg in call.Arguments)
        {
            callArgs.Add(item: RewriteExpression(expression: arg,
                scope: scope,
                inheritedGenericParameters: inheritedGenericParameters,
                inheritedGenericConstraints: inheritedGenericConstraints,
                includeMe: includeMe));
        }

        return CopyResolvedType(rewritten: call with
            {
                Callee = new IdentifierExpression(Name: liftedName, Location: lambda.Location),
                Arguments = callArgs
            },
            original: call);
    }

    private static Dictionary<string, TypeInfo> CollectCaptureTypesFromBody(Expression body,
        List<string> captureNames)
    {
        var targets =
            new HashSet<string>(collection: captureNames, comparer: StringComparer.Ordinal);
        var result = new Dictionary<string, TypeInfo>(comparer: StringComparer.Ordinal);
        ScanExprForIdentifierTypes(expr: body, targets: targets, result: result);
        return result;
    }

    private static void ScanExprForIdentifierTypes(Expression expr, HashSet<string> targets,
        Dictionary<string, TypeInfo> result)
    {
        if (result.Count == targets.Count)
        {
            return;
        }

        if (expr is IdentifierExpression id && targets.Contains(item: id.Name) &&
            id.ResolvedType != null)
        {
            result.TryAdd(key: id.Name, value: id.ResolvedType);
            if (result.Count == targets.Count)
            {
                return;
            }
        }

        foreach (Expression child in GetSubExpressions(expr: expr))
        {
            ScanExprForIdentifierTypes(expr: child, targets: targets, result: result);
            if (result.Count == targets.Count)
            {
                return;
            }
        }
    }

    private static IEnumerable<Expression> GetSubExpressions(Expression expr)
    {
        foreach (Expression sub in GetSubExpressionsCore(expr: expr))
        {
            yield return sub;
        }

        foreach (Expression sub in GetSubExpressionsExtended(expr: expr))
        {
            yield return sub;
        }
    }

    private static IEnumerable<Expression> GetSubExpressionsCore(Expression expr)
    {
        switch (expr)
        {
            case LambdaExpression:
                yield break;
            case BinaryExpression b:
                yield return b.Left;
                yield return b.Right;
                break;
            case UnaryExpression u:
                yield return u.Operand;
                break;
            case CallExpression c:
                yield return c.Callee;
                foreach (Expression arg in c.Arguments)
                {
                    yield return arg;
                }

                break;
            case MemberExpression m:
                yield return m.Object;
                break;
            case OptionalMemberExpression o:
                yield return o.Object;
                break;
            case IndexExpression i:
                yield return i.Object;
                yield return i.Index;
                break;
            case ConditionalExpression c:
                yield return c.Condition;
                yield return c.TrueExpression;
                yield return c.FalseExpression;
                break;
            case RangeExpression r:
                yield return r.Start;
                yield return r.End;
                if (r.Step != null)
                {
                    yield return r.Step;
                }

                break;
            case CreatorExpression c:
                foreach ((_, Expression val) in c.MemberVariables)
                {
                    yield return val;
                }

                break;
        }
    }

    private static IEnumerable<Expression> GetSubExpressionsExtended(Expression expr)
    {
        switch (expr)
        {
            case NamedArgumentExpression n:
                yield return n.Value;
                break;
            case ListLiteralExpression l:
                foreach (Expression e in l.Elements)
                {
                    yield return e;
                }

                break;
            case SetLiteralExpression s:
                foreach (Expression e in s.Elements)
                {
                    yield return e;
                }

                break;
            case DictLiteralExpression d:
                foreach ((Expression k, Expression v) in d.Pairs)
                {
                    yield return k;
                    yield return v;
                }

                break;
            case TupleLiteralExpression t:
                foreach (Expression e in t.Elements)
                {
                    yield return e;
                }

                break;
            case BlockExpression b:
                yield return b.Value;
                break;
            case StealExpression s:
                yield return s.Operand;
                break;
            case TypeConversionExpression c:
                yield return c.Expression;
                break;
            case GenericMemberRoutineCallExpression g:
                yield return g.Object;
                foreach (Expression arg in g.Arguments)
                {
                    yield return arg;
                }

                break;
            case GenericMemberExpression g:
                yield return g.Object;
                break;
            case InsertedTextExpression ins:
                foreach (Expression e in GetInsertedTextExpressions(parts: ins.Parts))
                {
                    yield return e;
                }

                break;
            case WhenExpression we:
                if (we.Expression != null)
                {
                    yield return we.Expression;
                }

                break;
        }
    }

    /// <summary>Yields the sub-expressions embedded inside an inserted-text part list.</summary>
    private static IEnumerable<Expression> GetInsertedTextExpressions(
        IEnumerable<InsertedTextPart> parts)
    {
        foreach (InsertedTextPart part in parts)
        {
            if (part is ExpressionPart ep)
            {
                yield return ep.Expression;
            }
        }
    }

    private Pattern RewritePatternExpressions(Pattern pattern, HashSet<string> scope,
        List<string>? inheritedGenericParameters,
        List<GenericConstraintDeclaration>? inheritedGenericConstraints, bool includeMe)
    {
        return pattern switch
        {
            ExpressionPattern expressionPattern => expressionPattern with
            {
                Expression = RewritePatternExpression(expression: expressionPattern.Expression,
                    scope: scope,
                    inheritedGenericParameters: inheritedGenericParameters,
                    inheritedGenericConstraints: inheritedGenericConstraints,
                    includeMe: includeMe)
            },
            ComparisonPattern comparison => comparison with
            {
                Value = RewritePatternExpression(expression: comparison.Value,
                    scope: scope,
                    inheritedGenericParameters: inheritedGenericParameters,
                    inheritedGenericConstraints: inheritedGenericConstraints,
                    includeMe: includeMe)
            },
            GuardPattern guard => guard with
            {
                InnerPattern = RewritePatternExpressions(pattern: guard.InnerPattern,
                    scope: scope,
                    inheritedGenericParameters: inheritedGenericParameters,
                    inheritedGenericConstraints: inheritedGenericConstraints,
                    includeMe: includeMe),
                Guard = RewritePatternExpression(expression: guard.Guard,
                    scope: scope,
                    inheritedGenericParameters: inheritedGenericParameters,
                    inheritedGenericConstraints: inheritedGenericConstraints,
                    includeMe: includeMe)
            },
            _ => pattern
        };
    }

    private Expression RewritePatternExpression(Expression expression, HashSet<string> scope,
        List<string>? inheritedGenericParameters,
        List<GenericConstraintDeclaration>? inheritedGenericConstraints, bool includeMe)
    {
        return RewriteExpression(expression: expression,
            scope: scope,
            inheritedGenericParameters: inheritedGenericParameters,
            inheritedGenericConstraints: inheritedGenericConstraints,
            includeMe: includeMe);
    }

    private static Expression CopyResolvedType<TExpression>(TExpression rewritten,
        TExpression original) where TExpression : Expression
    {
        rewritten.ResolvedType = original.ResolvedType;
        return rewritten;
    }

    private static HashSet<string> BuildRoutineScope(RoutineDeclaration routine, bool includeMe)
    {
        var scope = new HashSet<string>(comparer: StringComparer.Ordinal);
        foreach (Parameter parameter in routine.Parameters)
        {
            scope.Add(item: parameter.Name);
        }

        if (includeMe)
        {
            scope.Add(item: "me");
        }

        return scope;
    }

    private static void AddStatementBindings(HashSet<string> scope, Statement statement)
    {
        switch (statement)
        {
            case DeclarationStatement { Declaration: VariableDeclaration variable }:
                scope.Add(item: variable.Name);
                break;

            case DestructuringStatement destructuring:
                foreach (string binding in GetPatternBindings(pattern: destructuring.Pattern))
                {
                    scope.Add(item: binding);
                }

                break;
        }
    }

    private static List<string> MergeGenericParameters(List<string>? a, List<string>? b)
    {
        if (a == null && b == null)
        {
            return [];
        }

        var merged = new List<string>();
        merged.AddRange(
            collection: (a ?? []).Where(predicate: item => !merged.Contains(item: item)));
        merged.AddRange(
            collection: (b ?? []).Where(predicate: item => !merged.Contains(item: item)));
        return merged;
    }

    private static List<GenericConstraintDeclaration> MergeGenericConstraints(
        List<GenericConstraintDeclaration>? a, List<GenericConstraintDeclaration>? b)
    {
        var merged = new List<GenericConstraintDeclaration>();
        merged.AddRange(
            collection: (a ?? []).Where(predicate: item => !merged.Contains(item: item)));
        merged.AddRange(
            collection: (b ?? []).Where(predicate: item => !merged.Contains(item: item)));
        return merged;
    }

    private static List<Parameter> BuildLiftedParameters(LambdaExpression lambda,
        RoutineTypeInfo routineType)
    {
        var parameters = new List<Parameter>(capacity: lambda.Parameters.Count);
        for (int i = 0; i < lambda.Parameters.Count; i++)
        {
            Parameter sourceParam = lambda.Parameters[index: i];
            TypeInfo paramType = i < routineType.ParameterTypes.Count
                ? routineType.ParameterTypes[index: i]
                : ErrorTypeInfo.Instance;
            parameters.Add(item: sourceParam with
            {
                Type = TypeInfoToTypeExpression(type: paramType,
                    location: sourceParam.Location),
                DefaultValue = null
            });
        }

        return parameters;
    }

    private static List<ParameterInfo> BuildLiftedParameterInfos(LambdaExpression lambda,
        RoutineTypeInfo routineType)
    {
        var parameters = new List<ParameterInfo>(capacity: lambda.Parameters.Count);
        for (int i = 0; i < lambda.Parameters.Count; i++)
        {
            TypeInfo paramType = i < routineType.ParameterTypes.Count
                ? routineType.ParameterTypes[index: i]
                : ErrorTypeInfo.Instance;
            parameters.Add(item: new ParameterInfo(name: lambda.Parameters[index: i].Name,
                type: paramType));
        }

        return parameters;
    }

    private static TypeExpression TypeInfoToTypeExpression(TypeInfo type, SourceLocation location)
    {
        string baseName = type switch
        {
            RecordTypeInfo { GenericDefinition: not null } record => record.GenericDefinition.Name,
            EntityTypeInfo { GenericDefinition: not null } entity => entity.GenericDefinition.Name,
            ProtocolTypeInfo { GenericDefinition: not null } protocol => protocol.GenericDefinition
               .Name,
            _ => type.IsGenericResolution
                ? type.BareName
                : type.Name
        };

        List<TypeExpression>? args = type.TypeArguments is { Count: > 0 }
            ? type.TypeArguments
                  .Select(selector: arg => TypeInfoToTypeExpression(type: arg, location: location))
                  .ToList()
            : null;
        return new TypeExpression(Name: baseName, GenericArguments: args, Location: location);
    }

    private static HashSet<string> CollectLocalCaptures(LambdaExpression lambda,
        HashSet<string> outerScope)
    {
        var captures = new HashSet<string>(comparer: StringComparer.Ordinal);
        var parameterNames = lambda.Parameters
                                   .Select(selector: parameter => parameter.Name)
                                   .ToHashSet(comparer: StringComparer.Ordinal);

        CollectLocalCapturesRecursive(expression: lambda.Body,
            outerScope: outerScope,
            parameterNames: parameterNames,
            captures: captures);
        return captures;
    }

    private static void CollectLocalCapturesRecursive(Expression expression,
        HashSet<string> outerScope, HashSet<string> parameterNames, HashSet<string> captures)
    {
        switch (expression)
        {
            case IdentifierExpression identifier when outerScope.Contains(item: identifier.Name) &&
                                                      !parameterNames.Contains(
                                                          item: identifier.Name):
                captures.Add(item: identifier.Name);
                break;

            case LambdaExpression:
                return;

            case CompoundAssignmentExpression compound:
                CollectCapturesInCompound(compound: compound,
                    outerScope: outerScope,
                    parameterNames: parameterNames,
                    captures: captures);
                break;

            case BinaryExpression binary:
                CollectLocalCapturesRecursive(expression: binary.Left,
                    outerScope: outerScope,
                    parameterNames: parameterNames,
                    captures: captures);
                CollectLocalCapturesRecursive(expression: binary.Right,
                    outerScope: outerScope,
                    parameterNames: parameterNames,
                    captures: captures);
                break;

            case UnaryExpression unary:
                CollectLocalCapturesRecursive(expression: unary.Operand,
                    outerScope: outerScope,
                    parameterNames: parameterNames,
                    captures: captures);
                break;

            case CallExpression call:
                CollectCapturesInCall(call: call,
                    outerScope: outerScope,
                    parameterNames: parameterNames,
                    captures: captures);
                break;

            case MemberExpression member:
                CollectLocalCapturesRecursive(expression: member.Object,
                    outerScope: outerScope,
                    parameterNames: parameterNames,
                    captures: captures);
                break;

            case OptionalMemberExpression optionalMember:
                CollectLocalCapturesRecursive(expression: optionalMember.Object,
                    outerScope: outerScope,
                    parameterNames: parameterNames,
                    captures: captures);
                break;

            case IndexExpression index:
                CollectLocalCapturesRecursive(expression: index.Object,
                    outerScope: outerScope,
                    parameterNames: parameterNames,
                    captures: captures);
                CollectLocalCapturesRecursive(expression: index.Index,
                    outerScope: outerScope,
                    parameterNames: parameterNames,
                    captures: captures);
                break;

            case ConditionalExpression conditional:
                CollectCapturesInConditional(conditional: conditional,
                    outerScope: outerScope,
                    parameterNames: parameterNames,
                    captures: captures);
                break;

            case RangeExpression range:
                CollectCapturesInRange(range: range,
                    outerScope: outerScope,
                    parameterNames: parameterNames,
                    captures: captures);
                break;

            default:
                CollectLocalCapturesRecursiveExtended(expression: expression,
                    outerScope: outerScope,
                    parameterNames: parameterNames,
                    captures: captures);
                break;
        }
    }

    private static void CollectCapturesInCompound(CompoundAssignmentExpression compound,
        HashSet<string> outerScope, HashSet<string> parameterNames, HashSet<string> captures)
    {
        CollectLocalCapturesRecursive(expression: compound.Target,
            outerScope: outerScope,
            parameterNames: parameterNames,
            captures: captures);
        CollectLocalCapturesRecursive(expression: compound.Value,
            outerScope: outerScope,
            parameterNames: parameterNames,
            captures: captures);
    }

    private static void CollectCapturesInCall(CallExpression call, HashSet<string> outerScope,
        HashSet<string> parameterNames, HashSet<string> captures)
    {
        CollectLocalCapturesRecursive(expression: call.Callee,
            outerScope: outerScope,
            parameterNames: parameterNames,
            captures: captures);
        foreach (Expression argument in call.Arguments)
        {
            CollectLocalCapturesRecursive(expression: argument,
                outerScope: outerScope,
                parameterNames: parameterNames,
                captures: captures);
        }
    }

    private static void CollectCapturesInConditional(ConditionalExpression conditional,
        HashSet<string> outerScope, HashSet<string> parameterNames, HashSet<string> captures)
    {
        CollectLocalCapturesRecursive(expression: conditional.Condition,
            outerScope: outerScope,
            parameterNames: parameterNames,
            captures: captures);
        CollectLocalCapturesRecursive(expression: conditional.TrueExpression,
            outerScope: outerScope,
            parameterNames: parameterNames,
            captures: captures);
        CollectLocalCapturesRecursive(expression: conditional.FalseExpression,
            outerScope: outerScope,
            parameterNames: parameterNames,
            captures: captures);
    }

    private static void CollectCapturesInRange(RangeExpression range, HashSet<string> outerScope,
        HashSet<string> parameterNames, HashSet<string> captures)
    {
        CollectLocalCapturesRecursive(expression: range.Start,
            outerScope: outerScope,
            parameterNames: parameterNames,
            captures: captures);
        CollectLocalCapturesRecursive(expression: range.End,
            outerScope: outerScope,
            parameterNames: parameterNames,
            captures: captures);
        if (range.Step != null)
        {
            CollectLocalCapturesRecursive(expression: range.Step,
                outerScope: outerScope,
                parameterNames: parameterNames,
                captures: captures);
        }
    }

    // Second-tier dispatch for expression kinds that are less common or structurally simpler.
    private static void CollectLocalCapturesRecursiveExtended(Expression expression,
        HashSet<string> outerScope, HashSet<string> parameterNames, HashSet<string> captures)
    {
        switch (expression)
        {
            case CreatorExpression creator:
                foreach ((_, Expression value) in creator.MemberVariables)
                {
                    CollectLocalCapturesRecursive(expression: value,
                        outerScope: outerScope,
                        parameterNames: parameterNames,
                        captures: captures);
                }

                break;

            case WithExpression withExpr:
                CollectCapturesInWith(withExpr: withExpr,
                    outerScope: outerScope,
                    parameterNames: parameterNames,
                    captures: captures);
                break;

            case GenericMemberRoutineCallExpression genericCall:
                CollectLocalCapturesRecursive(expression: genericCall.Object,
                    outerScope: outerScope,
                    parameterNames: parameterNames,
                    captures: captures);
                foreach (Expression argument in genericCall.Arguments)
                {
                    CollectLocalCapturesRecursive(expression: argument,
                        outerScope: outerScope,
                        parameterNames: parameterNames,
                        captures: captures);
                }

                break;

            case GenericMemberExpression genericMember:
                CollectLocalCapturesRecursive(expression: genericMember.Object,
                    outerScope: outerScope,
                    parameterNames: parameterNames,
                    captures: captures);
                break;

            case NamedArgumentExpression namedArgument:
                CollectLocalCapturesRecursive(expression: namedArgument.Value,
                    outerScope: outerScope,
                    parameterNames: parameterNames,
                    captures: captures);
                break;

            case ListLiteralExpression list:
                foreach (Expression element in list.Elements)
                {
                    CollectLocalCapturesRecursive(expression: element,
                        outerScope: outerScope,
                        parameterNames: parameterNames,
                        captures: captures);
                }

                break;

            case SetLiteralExpression set:
                foreach (Expression element in set.Elements)
                {
                    CollectLocalCapturesRecursive(expression: element,
                        outerScope: outerScope,
                        parameterNames: parameterNames,
                        captures: captures);
                }

                break;

            case DictLiteralExpression dict:
                foreach ((Expression key, Expression value) in dict.Pairs)
                {
                    CollectLocalCapturesRecursive(expression: key,
                        outerScope: outerScope,
                        parameterNames: parameterNames,
                        captures: captures);
                    CollectLocalCapturesRecursive(expression: value,
                        outerScope: outerScope,
                        parameterNames: parameterNames,
                        captures: captures);
                }

                break;

            case TupleLiteralExpression tuple:
                foreach (Expression element in tuple.Elements)
                {
                    CollectLocalCapturesRecursive(expression: element,
                        outerScope: outerScope,
                        parameterNames: parameterNames,
                        captures: captures);
                }

                break;

            case TypeConversionExpression conversion:
                CollectLocalCapturesRecursive(expression: conversion.Expression,
                    outerScope: outerScope,
                    parameterNames: parameterNames,
                    captures: captures);
                break;

            case ChainedComparisonExpression chained:
                foreach (Expression operand in chained.Operands)
                {
                    CollectLocalCapturesRecursive(expression: operand,
                        outerScope: outerScope,
                        parameterNames: parameterNames,
                        captures: captures);
                }

                break;

            case BlockExpression block:
                CollectLocalCapturesRecursive(expression: block.Value,
                    outerScope: outerScope,
                    parameterNames: parameterNames,
                    captures: captures);
                break;

            case DictEntryLiteralExpression dictEntry:
                CollectLocalCapturesRecursive(expression: dictEntry.Key,
                    outerScope: outerScope,
                    parameterNames: parameterNames,
                    captures: captures);
                CollectLocalCapturesRecursive(expression: dictEntry.Value,
                    outerScope: outerScope,
                    parameterNames: parameterNames,
                    captures: captures);
                break;

            case IsPatternExpression isPattern:
                CollectLocalCapturesRecursive(expression: isPattern.Expression,
                    outerScope: outerScope,
                    parameterNames: parameterNames,
                    captures: captures);
                CollectLocalCapturesInPattern(pattern: isPattern.Pattern,
                    outerScope: outerScope,
                    parameterNames: parameterNames,
                    captures: captures);
                break;

            case FlagsTestExpression flagsTest:
                CollectLocalCapturesRecursive(expression: flagsTest.Subject,
                    outerScope: outerScope,
                    parameterNames: parameterNames,
                    captures: captures);
                break;

            case InsertedTextExpression inserted:
                CollectLocalCapturesInInsertedText(parts: inserted.Parts,
                    outerScope: outerScope,
                    parameterNames: parameterNames,
                    captures: captures);
                break;

            case StealExpression steal:
                CollectLocalCapturesRecursive(expression: steal.Operand,
                    outerScope: outerScope,
                    parameterNames: parameterNames,
                    captures: captures);
                break;

            case WaitforExpression waitfor:
                CollectLocalCapturesRecursive(expression: waitfor.Operand,
                    outerScope: outerScope,
                    parameterNames: parameterNames,
                    captures: captures);
                if (waitfor.Timeout != null)
                {
                    CollectLocalCapturesRecursive(expression: waitfor.Timeout,
                        outerScope: outerScope,
                        parameterNames: parameterNames,
                        captures: captures);
                }

                break;

            case DependentWaitforExpression dependentWaitfor:
                CollectCapturesInDependentWaitfor(dependentWaitfor: dependentWaitfor,
                    outerScope: outerScope,
                    parameterNames: parameterNames,
                    captures: captures);
                break;

            case CarrierPayloadExpression payload:
                CollectLocalCapturesRecursive(expression: payload.Carrier,
                    outerScope: outerScope,
                    parameterNames: parameterNames,
                    captures: captures);
                break;

            case BackIndexExpression backIndex:
                CollectLocalCapturesRecursive(expression: backIndex.Operand,
                    outerScope: outerScope,
                    parameterNames: parameterNames,
                    captures: captures);
                break;

            case WhenExpression whenExpr:
                CollectCapturesInWhenExpression(whenExpr: whenExpr,
                    outerScope: outerScope,
                    parameterNames: parameterNames,
                    captures: captures);
                break;
        }
    }

    /// <summary>
    /// Collects local captures from expression parts inside an inserted-text expression, visiting
    /// only the sub-expressions embedded in interpolation holes (not plain text parts).
    /// </summary>
    private static void CollectLocalCapturesInInsertedText(IEnumerable<InsertedTextPart> parts,
        HashSet<string> outerScope, HashSet<string> parameterNames, HashSet<string> captures)
    {
        foreach (InsertedTextPart part in parts)
        {
            if (part is ExpressionPart expressionPart)
            {
                CollectLocalCapturesRecursive(expression: expressionPart.Expression,
                    outerScope: outerScope,
                    parameterNames: parameterNames,
                    captures: captures);
            }
        }
    }

    private static void CollectCapturesInWith(WithExpression withExpr, HashSet<string> outerScope,
        HashSet<string> parameterNames, HashSet<string> captures)
    {
        CollectLocalCapturesRecursive(expression: withExpr.Base,
            outerScope: outerScope,
            parameterNames: parameterNames,
            captures: captures);
        foreach ((_, Expression? index, Expression value) in withExpr.Updates)
        {
            CollectLocalCapturesRecursive(expression: value,
                outerScope: outerScope,
                parameterNames: parameterNames,
                captures: captures);
            if (index != null)
            {
                CollectLocalCapturesRecursive(expression: index,
                    outerScope: outerScope,
                    parameterNames: parameterNames,
                    captures: captures);
            }
        }
    }

    private static void CollectCapturesInDependentWaitfor(
        DependentWaitforExpression dependentWaitfor, HashSet<string> outerScope,
        HashSet<string> parameterNames, HashSet<string> captures)
    {
        CollectLocalCapturesRecursive(expression: dependentWaitfor.Operand,
            outerScope: outerScope,
            parameterNames: parameterNames,
            captures: captures);
        foreach (TaskDependency dependency in dependentWaitfor.Dependencies)
        {
            CollectLocalCapturesRecursive(expression: dependency.DependencyExpr,
                outerScope: outerScope,
                parameterNames: parameterNames,
                captures: captures);
        }

        if (dependentWaitfor.Timeout != null)
        {
            CollectLocalCapturesRecursive(expression: dependentWaitfor.Timeout,
                outerScope: outerScope,
                parameterNames: parameterNames,
                captures: captures);
        }
    }

    private static void CollectCapturesInWhenExpression(WhenExpression whenExpr,
        HashSet<string> outerScope, HashSet<string> parameterNames, HashSet<string> captures)
    {
        if (whenExpr.Expression != null)
        {
            CollectLocalCapturesRecursive(expression: whenExpr.Expression,
                outerScope: outerScope,
                parameterNames: parameterNames,
                captures: captures);
        }

        foreach (WhenClause clause in whenExpr.Clauses)
        {
            CollectLocalCapturesInStatement(statement: clause.Body,
                outerScope: outerScope,
                parameterNames: parameterNames,
                captures: captures);
        }
    }

    private static void CollectLocalCapturesInStatement(Statement statement,
        HashSet<string> outerScope, HashSet<string> parameterNames, HashSet<string> captures)
    {
        switch (statement)
        {
            case BlockStatement block:
                foreach (Statement child in block.Statements)
                {
                    CollectLocalCapturesInStatement(statement: child,
                        outerScope: outerScope,
                        parameterNames: parameterNames,
                        captures: captures);
                }

                break;
            case IfStatement ifs:
                CollectLocalCapturesRecursive(expression: ifs.Condition,
                    outerScope: outerScope,
                    parameterNames: parameterNames,
                    captures: captures);
                CollectLocalCapturesInStatement(statement: ifs.ThenStatement,
                    outerScope: outerScope,
                    parameterNames: parameterNames,
                    captures: captures);
                if (ifs.ElseStatement != null)
                {
                    CollectLocalCapturesInStatement(statement: ifs.ElseStatement,
                        outerScope: outerScope,
                        parameterNames: parameterNames,
                        captures: captures);
                }

                break;
            case WhileStatement whileStmt:
                CollectLocalCapturesRecursive(expression: whileStmt.Condition,
                    outerScope: outerScope,
                    parameterNames: parameterNames,
                    captures: captures);
                CollectLocalCapturesInStatement(statement: whileStmt.Body,
                    outerScope: outerScope,
                    parameterNames: parameterNames,
                    captures: captures);
                if (whileStmt.ElseBranch != null)
                {
                    CollectLocalCapturesInStatement(statement: whileStmt.ElseBranch,
                        outerScope: outerScope,
                        parameterNames: parameterNames,
                        captures: captures);
                }

                break;
            case LoopStatement loop:
                CollectLocalCapturesInStatement(statement: loop.Body,
                    outerScope: outerScope,
                    parameterNames: parameterNames,
                    captures: captures);
                break;
            case EachStatement eachStmt:
                CollectLocalCapturesRecursive(expression: eachStmt.Iterable,
                    outerScope: outerScope,
                    parameterNames: parameterNames,
                    captures: captures);
                CollectLocalCapturesInStatement(statement: eachStmt.Body,
                    outerScope: outerScope,
                    parameterNames: parameterNames,
                    captures: captures);
                if (eachStmt.ElseBranch != null)
                {
                    CollectLocalCapturesInStatement(statement: eachStmt.ElseBranch,
                        outerScope: outerScope,
                        parameterNames: parameterNames,
                        captures: captures);
                }

                break;
            case WhenStatement whenStmt:
                CollectLocalCapturesRecursive(expression: whenStmt.Expression,
                    outerScope: outerScope,
                    parameterNames: parameterNames,
                    captures: captures);
                foreach (WhenClause clause in whenStmt.Clauses)
                {
                    CollectLocalCapturesInStatement(statement: clause.Body,
                        outerScope: outerScope,
                        parameterNames: parameterNames,
                        captures: captures);
                }

                break;
            case UsingStatement usingStmt:
                CollectLocalCapturesRecursive(expression: usingStmt.Resource,
                    outerScope: outerScope,
                    parameterNames: parameterNames,
                    captures: captures);
                CollectLocalCapturesInStatement(statement: usingStmt.Body,
                    outerScope: outerScope,
                    parameterNames: parameterNames,
                    captures: captures);
                if (usingStmt.FallbackBody != null)
                {
                    CollectLocalCapturesInStatement(statement: usingStmt.FallbackBody,
                        outerScope: outerScope,
                        parameterNames: parameterNames,
                        captures: captures);
                }

                break;
            case DangerStatement danger:
                CollectLocalCapturesInStatement(statement: danger.Body,
                    outerScope: outerScope,
                    parameterNames: parameterNames,
                    captures: captures);
                break;
            case DeclarationStatement
            {
                Declaration: VariableDeclaration { Initializer: not null } variable
            }:
                CollectLocalCapturesRecursive(expression: variable.Initializer,
                    outerScope: outerScope,
                    parameterNames: parameterNames,
                    captures: captures);
                break;
            case AssignmentStatement assignment:
                CollectLocalCapturesRecursive(expression: assignment.Target,
                    outerScope: outerScope,
                    parameterNames: parameterNames,
                    captures: captures);
                CollectLocalCapturesRecursive(expression: assignment.Value,
                    outerScope: outerScope,
                    parameterNames: parameterNames,
                    captures: captures);
                break;
            case ReturnStatement { Value: not null } ret:
                CollectLocalCapturesRecursive(expression: ret.Value,
                    outerScope: outerScope,
                    parameterNames: parameterNames,
                    captures: captures);
                break;
            case ExpressionStatement expressionStatement:
                CollectLocalCapturesRecursive(expression: expressionStatement.Expression,
                    outerScope: outerScope,
                    parameterNames: parameterNames,
                    captures: captures);
                break;
            case DiscardStatement discard:
                CollectLocalCapturesRecursive(expression: discard.Expression,
                    outerScope: outerScope,
                    parameterNames: parameterNames,
                    captures: captures);
                break;
            case BecomesStatement becomes:
                CollectLocalCapturesRecursive(expression: becomes.Value,
                    outerScope: outerScope,
                    parameterNames: parameterNames,
                    captures: captures);
                break;
            case ThrowStatement throwStmt:
                CollectLocalCapturesRecursive(expression: throwStmt.Error,
                    outerScope: outerScope,
                    parameterNames: parameterNames,
                    captures: captures);
                break;
            case VariantReturnStatement { Value: not null } variantReturn:
                CollectLocalCapturesRecursive(expression: variantReturn.Value,
                    outerScope: outerScope,
                    parameterNames: parameterNames,
                    captures: captures);
                break;
            case DestructuringStatement destructuring:
                CollectLocalCapturesRecursive(expression: destructuring.Initializer,
                    outerScope: outerScope,
                    parameterNames: parameterNames,
                    captures: captures);
                break;
        }
    }

    private static void CollectLocalCapturesInPattern(Pattern pattern, HashSet<string> outerScope,
        HashSet<string> parameterNames, HashSet<string> captures)
    {
        switch (pattern)
        {
            case ExpressionPattern expressionPattern:
                CollectLocalCapturesRecursive(expression: expressionPattern.Expression,
                    outerScope: outerScope,
                    parameterNames: parameterNames,
                    captures: captures);
                break;
            case ComparisonPattern comparison:
                CollectLocalCapturesRecursive(expression: comparison.Value,
                    outerScope: outerScope,
                    parameterNames: parameterNames,
                    captures: captures);
                break;
            case GuardPattern guard:
                CollectLocalCapturesInPattern(pattern: guard.InnerPattern,
                    outerScope: outerScope,
                    parameterNames: parameterNames,
                    captures: captures);
                CollectLocalCapturesRecursive(expression: guard.Guard,
                    outerScope: outerScope,
                    parameterNames: parameterNames,
                    captures: captures);
                break;
        }
    }

    private static bool ContainsIdentifier(Expression expression, string name)
    {
        var hits = new HashSet<string>(comparer: StringComparer.Ordinal);
        CollectLocalCapturesRecursive(expression: expression,
            outerScope: [name],
            parameterNames: [],
            captures: hits);
        return hits.Contains(item: name);
    }

    private static IEnumerable<string> GetPatternBindings(Pattern? pattern)
    {
        if (pattern == null)
        {
            yield break;
        }

        switch (pattern)
        {
            case IdentifierPattern identifier:
                yield return identifier.Name;
                break;

            case TypePattern { VariableName: not null } typePattern:
                yield return typePattern.VariableName;
                foreach (string binding in
                         GetDestructuringBindings(bindings: typePattern.Bindings))
                {
                    yield return binding;
                }

                break;

            case VariantPattern variantPattern:
                foreach (string binding in GetDestructuringBindings(
                             bindings: variantPattern.Bindings))
                {
                    yield return binding;
                }

                break;

            case CrashablePattern { VariableName: not null } crashablePattern:
                yield return crashablePattern.VariableName;
                break;

            case ElsePattern { VariableName: not null } elsePattern:
                yield return elsePattern.VariableName;
                break;

            case DestructuringPattern destructuring:
                foreach (string binding in GetDestructuringBindings(
                             bindings: destructuring.Bindings))
                {
                    yield return binding;
                }

                break;

            case GuardPattern guard:
                foreach (string binding in GetPatternBindings(pattern: guard.InnerPattern))
                {
                    yield return binding;
                }

                break;
        }
    }

    private static IEnumerable<string> GetDestructuringBindings(
        List<DestructuringBinding>? bindings)
    {
        if (bindings == null)
        {
            yield break;
        }

        foreach (DestructuringBinding binding in bindings)
        {
            if (binding.BindingName != null)
            {
                yield return binding.BindingName;
            }

            if (binding.NestedPattern != null)
            {
                foreach (string nested in GetPatternBindings(pattern: binding.NestedPattern))
                {
                    yield return nested;
                }
            }
        }
    }
}
