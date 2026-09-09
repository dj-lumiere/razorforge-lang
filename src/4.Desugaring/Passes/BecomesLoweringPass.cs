using SyntaxTree;

namespace Compiler.Desugaring.Passes;

/// <summary>
/// Future Phase 8 pass: lower block-expression result flow (`becomes`) into explicit
/// temporaries and assignments after verification is complete.
/// </summary>
#pragma warning disable CS9113
internal sealed class BecomesLoweringPass(PostprocessingContext _) : AstRewriter
#pragma warning restore CS9113
{
    public void Run(Program program)
    {
        for (int i = 0; i < program.Declarations.Count; i++)
        {
            switch (program.Declarations[index: i])
            {
                case RoutineDeclaration routine:
                {
                    Statement lowered = VisitStatement(stmt: routine.Body);
                    if (!ReferenceEquals(objA: lowered, objB: routine.Body))
                    {
                        program.Declarations[index: i] = routine with { Body = lowered };
                    }

                    break;
                }

                case EntityDeclaration entity:
                    LowerMemberList(members: entity.Members);
                    break;

                case RecordDeclaration record:
                    LowerMemberList(members: record.Members);
                    break;

                case CrashableDeclaration crashable:
                    LowerMemberList(members: crashable.Members);
                    break;
            }
        }
    }

    private void LowerMemberList(List<SyntaxTree.Declaration> members)
    {
        for (int i = 0; i < members.Count; i++)
        {
            if (members[index: i] is not RoutineDeclaration routine)
            {
                continue;
            }

            Statement lowered = VisitStatement(stmt: routine.Body);
            if (!ReferenceEquals(objA: lowered, objB: routine.Body))
            {
                members[index: i] = routine with { Body = lowered };
            }
        }
    }

    /// <summary>
    /// The only node this pass rewrites: a block whose statements contain a synthetic `_wres_`
    /// result-target declaration immediately followed by a statement carrying `becomes` — the
    /// `becomes` statements in that following statement are rewritten into assignments to the
    /// target. All structural recursion is supplied by <see cref="AstRewriter"/>; the base rewrites
    /// the child statements FIRST (matching the original order), then the pairwise scan runs.
    /// </summary>
    protected override Statement VisitBlock(BlockStatement s)
    {
        // Recurse into children first (base returns the same reference when nothing changed).
        var lowered = (BlockStatement)base.VisitBlock(s: s);

        List<Statement>? loweredStatements = null;
        for (int i = 0; i < lowered.Statements.Count - 1; i++)
        {
            if (TryGetSyntheticWhenResultTarget(statement: lowered.Statements[index: i],
                    target: out IdentifierExpression? target) &&
                ContainsBecomes(statement: lowered.Statements[index: i + 1]))
            {
                loweredStatements ??= [.. lowered.Statements];
                loweredStatements[index: i + 1] =
                    RewriteBecomes(statement: loweredStatements[index: i + 1], target: target!);
            }
        }

        return loweredStatements != null
            ? lowered with { Statements = loweredStatements }
            : lowered;
    }

    private static bool TryGetSyntheticWhenResultTarget(Statement statement,
        out IdentifierExpression? target)
    {
        target = null;

        if (statement is not DeclarationStatement
            {
                Declaration: VariableDeclaration
                {
                    Initializer: null
                } variable
            } || !variable.Name.StartsWith(value: "_wres_",
                comparisonType: StringComparison.Ordinal))
        {
            return false;
        }

        target = new IdentifierExpression(Name: variable.Name, Location: variable.Location);
        return true;
    }

    private static bool ContainsBecomes(Statement statement)
    {
        return statement switch
        {
            BecomesStatement => true,
            BlockStatement block => block.Statements.Any(predicate: ContainsBecomes),
            IfStatement ifs => ContainsBecomes(statement: ifs.ThenStatement) ||
                               ifs.ElseStatement != null &&
                               ContainsBecomes(statement: ifs.ElseStatement),
            WhileStatement whileStmt => ContainsBecomes(statement: whileStmt.Body) ||
                                        whileStmt.ElseBranch != null &&
                                        ContainsBecomes(statement: whileStmt.ElseBranch),
            LoopStatement loop => ContainsBecomes(statement: loop.Body),
            EachStatement eachStmt => ContainsBecomes(statement: eachStmt.Body) ||
                                      eachStmt.ElseBranch != null &&
                                      ContainsBecomes(statement: eachStmt.ElseBranch),
            WhenStatement whenStmt => whenStmt.Clauses.Any(predicate: clause =>
                ContainsBecomes(statement: clause.Body)),
            DangerStatement danger => ContainsBecomes(statement: danger.Body),
            UsingStatement usingStmt => ContainsBecomes(statement: usingStmt.Body) ||
                                        usingStmt.FallbackBody != null &&
                                        ContainsBecomes(statement: usingStmt.FallbackBody),
            _ => false
        };
    }

    private static Statement RewriteBecomes(Statement statement, IdentifierExpression target)
    {
        return statement switch
        {
            BecomesStatement becomes => new AssignmentStatement(Target: target,
                Value: becomes.Value,
                Location: becomes.Location),
            BlockStatement block => block with
            {
                Statements = block.Statements
                                  .Select(selector: stmt =>
                                       RewriteBecomes(statement: stmt, target: target))
                                  .ToList()
            },
            IfStatement ifs => ifs with
            {
                ThenStatement = RewriteBecomes(statement: ifs.ThenStatement, target: target),
                ElseStatement = ifs.ElseStatement != null
                    ? RewriteBecomes(statement: ifs.ElseStatement, target: target)
                    : null
            },
            WhileStatement whileStmt => whileStmt with
            {
                Body = RewriteBecomes(statement: whileStmt.Body, target: target),
                ElseBranch = whileStmt.ElseBranch != null
                    ? RewriteBecomes(statement: whileStmt.ElseBranch, target: target)
                    : null
            },
            LoopStatement loop => loop with
            {
                Body = RewriteBecomes(statement: loop.Body, target: target)
            },
            EachStatement eachStmt => eachStmt with
            {
                Body = RewriteBecomes(statement: eachStmt.Body, target: target),
                ElseBranch = eachStmt.ElseBranch != null
                    ? RewriteBecomes(statement: eachStmt.ElseBranch, target: target)
                    : null
            },
            WhenStatement whenStmt => whenStmt with
            {
                Clauses = whenStmt.Clauses
                                  .Select(selector: clause => clause with
                                   {
                                       Body = RewriteBecomes(statement: clause.Body,
                                           target: target)
                                   })
                                  .ToList()
            },
            DangerStatement danger => danger with
            {
                Body = (BlockStatement)RewriteBecomes(statement: danger.Body, target: target)
            },
            UsingStatement usingStmt => usingStmt with
            {
                Body = RewriteBecomes(statement: usingStmt.Body, target: target),
                FallbackBody = usingStmt.FallbackBody != null
                    ? RewriteBecomes(statement: usingStmt.FallbackBody, target: target)
                    : null
            },
            _ => statement
        };
    }
}
