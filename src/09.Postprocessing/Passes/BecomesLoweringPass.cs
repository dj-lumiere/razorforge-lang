using System;
using System.Collections.Generic;
using System.Linq;
using SyntaxTree;

namespace Compiler.Postprocessing.Passes;

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
            switch (program.Declarations[i])
            {
                case RoutineDeclaration routine:
                {
                    Statement lowered = VisitStatement(routine.Body);
                    if (!ReferenceEquals(lowered, routine.Body))
                    {
                        program.Declarations[i] = routine with { Body = lowered };
                    }

                    break;
                }

                case EntityDeclaration entity:
                    LowerMemberList(entity.Members);
                    break;

                case RecordDeclaration record:
                    LowerMemberList(record.Members);
                    break;

                case CrashableDeclaration crashable:
                    LowerMemberList(crashable.Members);
                    break;
            }
        }
    }

    private void LowerMemberList(List<SyntaxTree.Declaration> members)
    {
        for (int i = 0; i < members.Count; i++)
        {
            if (members[i] is not RoutineDeclaration routine)
            {
                continue;
            }

            Statement lowered = VisitStatement(routine.Body);
            if (!ReferenceEquals(lowered, routine.Body))
            {
                members[i] = routine with { Body = lowered };
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
    protected override Statement VisitBlock(BlockStatement block)
    {
        // Recurse into children first (base returns the same reference when nothing changed).
        var lowered = (BlockStatement)base.VisitBlock(s: block);

        List<Statement>? loweredStatements = null;
        for (int i = 0; i < lowered.Statements.Count - 1; i++)
        {
            if (TryGetSyntheticWhenResultTarget(lowered.Statements[i], out IdentifierExpression? target) &&
                ContainsBecomes(lowered.Statements[i + 1]))
            {
                loweredStatements ??= [..lowered.Statements];
                loweredStatements[i + 1] = RewriteBecomes(loweredStatements[i + 1], target!);
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
            } ||
            !variable.Name.StartsWith(value: "_wres_", comparisonType: StringComparison.Ordinal))
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
            BlockStatement block => block.Statements.Any(ContainsBecomes),
            IfStatement ifs =>
                ContainsBecomes(ifs.ThenStatement) ||
                ifs.ElseStatement != null && ContainsBecomes(ifs.ElseStatement),
            WhileStatement whileStmt =>
                ContainsBecomes(whileStmt.Body) ||
                whileStmt.ElseBranch != null && ContainsBecomes(whileStmt.ElseBranch),
            LoopStatement loop => ContainsBecomes(loop.Body),
            EachStatement eachStmt =>
                ContainsBecomes(eachStmt.Body) ||
                eachStmt.ElseBranch != null && ContainsBecomes(eachStmt.ElseBranch),
            WhenStatement whenStmt => whenStmt.Clauses.Any(clause => ContainsBecomes(clause.Body)),
            DangerStatement danger => ContainsBecomes(danger.Body),
            UsingStatement usingStmt => ContainsBecomes(usingStmt.Body) ||
                usingStmt.FallbackBody != null && ContainsBecomes(usingStmt.FallbackBody),
            _ => false
        };
    }

    private static Statement RewriteBecomes(Statement statement, IdentifierExpression target)
    {
        return statement switch
        {
            BecomesStatement becomes => new AssignmentStatement(
                Target: target,
                Value: becomes.Value,
                Location: becomes.Location),
            BlockStatement block => block with
            {
                Statements = block.Statements
                    .Select(stmt => RewriteBecomes(stmt, target))
                    .ToList()
            },
            IfStatement ifs => ifs with
            {
                ThenStatement = RewriteBecomes(ifs.ThenStatement, target),
                ElseStatement = ifs.ElseStatement != null
                    ? RewriteBecomes(ifs.ElseStatement, target)
                    : null
            },
            WhileStatement whileStmt => whileStmt with
            {
                Body = RewriteBecomes(whileStmt.Body, target),
                ElseBranch = whileStmt.ElseBranch != null
                    ? RewriteBecomes(whileStmt.ElseBranch, target)
                    : null
            },
            LoopStatement loop => loop with
            {
                Body = RewriteBecomes(loop.Body, target)
            },
            EachStatement eachStmt => eachStmt with
            {
                Body = RewriteBecomes(eachStmt.Body, target),
                ElseBranch = eachStmt.ElseBranch != null
                    ? RewriteBecomes(eachStmt.ElseBranch, target)
                    : null
            },
            WhenStatement whenStmt => whenStmt with
            {
                Clauses = whenStmt.Clauses
                    .Select(clause => clause with
                    {
                        Body = RewriteBecomes(clause.Body, target)
                    })
                    .ToList()
            },
            DangerStatement danger => danger with
            {
                Body = (BlockStatement)RewriteBecomes(danger.Body, target)
            },
            UsingStatement usingStmt => usingStmt with
            {
                Body = RewriteBecomes(usingStmt.Body, target),
                FallbackBody = usingStmt.FallbackBody != null
                    ? RewriteBecomes(usingStmt.FallbackBody, target)
                    : null
            },
            _ => statement
        };
    }
}
