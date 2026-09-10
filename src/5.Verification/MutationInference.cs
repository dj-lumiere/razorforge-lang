using Builder.Declaration;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;
using Builder.Verification.Enums;

namespace Builder.Verification;

/// <summary>
/// Performs mutation inference for routines.
/// Implements the three-phase algorithm from the wiki:
///
/// Step 1 (Direct Analysis):
///   - If memberRoutine writes to any member variable of me -> mark as Writable
///   - If memberRoutine calls .modify() on me member variables -> mark as Writable
///
/// Step 2 (Call Graph Propagation):
///   - If memberRoutine calls a Writable memberRoutine on me -> mark as Writable
///   - If memberRoutine calls a Reshaping memberRoutine on me -> mark as Reshaping
///   - Repeat until fixpoint (no changes)
///
/// Step 3 (Token Checking):
///   - Viewing/Consulting tokens can only call Readonly memberRoutines
///   - Modifying/Amending tokens can call Readonly or Writable memberRoutines
///   - Only owned/non-token access can call Reshaping memberRoutines
/// </summary>
public sealed class MutationInference
{
    private readonly CallGraph _callGraph;

    /// <summary>
    /// Initializes a new instance of the <see cref="MutationInference"/> class.
    /// </summary>
    /// <param name="callGraph">The call graph to analyze.</param>
    /// <param name="registry">The type registry for lookups.</param>
    public MutationInference(CallGraph callGraph, TypeRegistry registry)
    {
        _callGraph = callGraph;
    }

    /// <summary>
    /// Runs the complete mutation inference algorithm.
    /// </summary>
    public void InferAll()
    {
        // Step 1: Direct analysis (already done during AST traversal)
        // The DirectlyMutates flag should be set on CallGraphNodes

        // Step 2: Call graph propagation
        PropagateCategories();
    }

    /// <summary>
    /// Step 2: Propagates mutation categories through the call graph.
    /// Uses fixpoint iteration until no changes occur.
    /// </summary>
    private void PropagateCategories()
    {
        bool changed = true;

        while (changed)
        {
            changed = false;

            foreach (CallGraphNode node in _callGraph.AllNodes)
            {
                MutationCategory originalCategory = node.InferredMutation;
                MutationCategory newCategory = ComputeCategory(node: node);

                if (newCategory <= originalCategory)
                {
                    continue;
                }

                node.InferredMutation = newCategory;
                changed = true;
            }
        }
    }

    /// <summary>
    /// Computes the mutation category for a node based on its direct mutations
    /// and the categories of memberRoutines it calls on 'me'.
    /// </summary>
    /// <param name="node">The node to compute the category for.</param>
    /// <returns>The computed mutation category.</returns>
    private static MutationCategory ComputeCategory(CallGraphNode node)
    {
        // Start from the declared floor — user annotations are never downgraded.
        MutationCategory category = node.Routine.DeclaredMutation;

        // Direct mutations
        if (node.DirectlyMutates && category < MutationCategory.Writable)
        {
            category = MutationCategory.Writable;
        }

        if (node.DirectlyMigrates && category < MutationCategory.Reshaping)
        {
            category = MutationCategory.Reshaping;
        }

        // Propagate from callees (only for calls on 'me')
        foreach (CallEdge edge in node.Callees)
        {
            if (!edge.CallsOnMe)
            {
                continue;
            }

            MutationCategory calleeCategory = edge.Target.InferredMutation;
            if (calleeCategory > category)
            {
                category = calleeCategory;
            }
        }

        return category;
    }

    /// <summary>
    /// Analyzes a statement for direct mutations (member variable writes to 'me').
    /// Call this during Step 1 (direct analysis) AST traversal.
    /// </summary>
    /// <param name="node">The call graph node for the current routine.</param>
    /// <param name="statement">The statement to analyze.</param>
    public static void AnalyzeStatementForMutation(CallGraphNode node, Statement statement)
    {
        switch (statement)
        {
            case AssignmentStatement assign:
                AnalyzeAssignmentForMutation(node: node, assignment: assign);
                break;

            case BlockStatement block:
                foreach (Statement stmt in block.Statements)
                {
                    AnalyzeStatementForMutation(node: node, statement: stmt);
                }

                break;

            case IfStatement ifStmt:
                AnalyzeStatementForMutation(node: node, statement: ifStmt.ThenStatement);
                if (ifStmt.ElseStatement != null)
                {
                    AnalyzeStatementForMutation(node: node, statement: ifStmt.ElseStatement);
                }

                break;

            case WhileStatement whileStmt:
                AnalyzeStatementForMutation(node: node, statement: whileStmt.Body);
                break;

            case LoopStatement loopStmt:
                AnalyzeStatementForMutation(node: node, statement: loopStmt.Body);
                break;

            case EachStatement eachStmt:
                AnalyzeStatementForMutation(node: node, statement: eachStmt.Body);
                break;

            // Other statement types don't directly mutate member variables
        }
    }

    /// <summary>
    /// Analyzes an assignment for direct mutations to 'me'.
    /// </summary>
    /// <param name="node">The call graph node.</param>
    /// <param name="assignment">The assignment statement.</param>
    private static void AnalyzeAssignmentForMutation(CallGraphNode node,
        AssignmentStatement assignment)
    {
        if (!IsMemberVariableOfMe(expression: assignment.Target))
        {
            return;
        }

        node.DirectlyMutates = true;
        node.InferredMutation = MutationCategory.Writable;

        // A direct write to a Hijacked[T] field relocates the buffer pointer — reshaping.
        if (assignment.Target is MemberExpression
            {
                Object: IdentifierExpression { Name: "me" }
            } direct && IsHijackedField(ownerType: node.Routine.OwnerType,
                fieldName: direct.MemberName))
        {
            node.DirectlyMigrates = true;
            node.InferredMutation = MutationCategory.Reshaping;
        }
    }

    /// <summary>
    /// Returns true if <paramref name="fieldName"/> is a <c>Hijacked[T]</c> field on <paramref name="ownerType"/>.
    /// </summary>
    private static bool IsHijackedField(TypeSymbol? ownerType, string fieldName)
    {
        List<MemberVariableInfo>? fields = ownerType switch
        {
            EntityTypeSymbol e => e.MemberVariables,
            RecordTypeSymbol r => r.MemberVariables,
            _ => null
        };

        MemberVariableInfo? field = fields?.FirstOrDefault(predicate: f => f.Name == fieldName);
        return field?.Type.Name.StartsWith(value: RuntimeContract.Hijacked,
            comparisonType: StringComparison.Ordinal) == true;
    }

    /// <summary>
    /// Checks if an expression is a member variable access on 'me' (directly or transitively).
    /// Examples: me.memberVar, me.memberVar.subfield, me.list[0]
    /// </summary>
    /// <param name="expression">The expression to check.</param>
    /// <returns>True if the expression accesses a member variable of 'me'.</returns>
    private static bool IsMemberVariableOfMe(Expression expression)
    {
        return expression switch
        {
            MemberExpression memberExpr =>
                memberExpr.Object is IdentifierExpression { Name: "me" } ||
                IsMemberVariableOfMe(expression: memberExpr.Object),

            IndexExpression indexExpr => IsMemberVariableOfMe(expression: indexExpr.Object),

            IdentifierExpression { Name: "me" } => true,

            _ => false
        };
    }
}
