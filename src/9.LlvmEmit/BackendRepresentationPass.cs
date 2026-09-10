using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using Compiler.Declaration;
using Compiler.Targeting;
using SyntaxTree;
using TypeModel.Types;

namespace Compiler.LlvmEmit;

/// <summary>
/// Computes <see cref="Expression.ResolvedRepr"/> for all backend-visible expressions
/// before backend-entry validation runs.
/// </summary>
public sealed class BackendRepresentationPass
{
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> ChildPropertyCache = new();
    private readonly TypeRegistry _registry;
    private readonly TargetConfig _target;

    /// <summary>
    /// Initializes a pass that annotates expressions with backend representation metadata.
    /// </summary>
    public BackendRepresentationPass(TypeRegistry registry, TargetConfig target)
    {
        _registry = registry;
        _target = target;
    }

    /// <summary>
    /// Annotates every typed expression in a program with its backend representation.
    /// </summary>
    public void Run(Program program)
    {
        Walk(node: program);
    }

    /// <summary>
    /// Annotates every typed expression in a synthesized or lowered statement body.
    /// </summary>
    public void Run(Statement statement)
    {
        Walk(node: statement);
    }

    /// <summary>
    /// Recursively visits a syntax tree node, attaching representation metadata before visiting children.
    /// </summary>
    private void Walk(ISyntaxTreeNode node)
    {
        if (node is Expression { ResolvedType: { } resolvedType and not ErrorTypeInfo } expression
            and not TypeExpression)
        {
            expression.ResolvedRepr = BackendReprResolver.Resolve(type: resolvedType,
                registry: _registry,
                target: _target);
        }

        foreach (ISyntaxTreeNode child in EnumerateChildren(node: node))
        {
            Walk(node: child);
        }
    }

    /// <summary>
    /// Enumerates child syntax nodes through public record properties so new AST node shapes are visited automatically.
    /// </summary>
    /// <remarks>
    /// Filters out known AST alias properties (e.g. <c>IfStatement.ThenBranch</c> which returns
    /// <c>ThenStatement</c>). Without the filter, nested <c>if</c> nodes would be visited twice
    /// per level, producing 2^depth work. Reference-equality dedupe was tried first but breaks
    /// when synthesized monomorphic bodies legitimately share child instances.
    /// </remarks>
    private static IEnumerable<ISyntaxTreeNode> EnumerateChildren(ISyntaxTreeNode node)
    {
        PropertyInfo[] properties = ChildPropertyCache.GetOrAdd(key: node.GetType(),
            valueFactory: static type => type
                                        .GetProperties(bindingAttr: BindingFlags.Instance |
                                             BindingFlags.Public)
                                        .Where(predicate: property =>
                                             property.Name != nameof(ISyntaxTreeNode.Location) &&
                                             property.CanRead && property.GetIndexParameters()
                                                .Length == 0 && !IsAstAliasProperty(
                                                 ownerType: type,
                                                 propertyName: property.Name))
                                        .ToArray());

        foreach (PropertyInfo property in properties)
        {
            object? value = property.GetValue(obj: node);
            switch (value)
            {
                case null:
                case string:
                    continue;
                case ISyntaxTreeNode childNode:
                    yield return childNode;
                    continue;
                case IEnumerable sequence:
                    foreach (object? item in sequence)
                    {
                        if (item is ISyntaxTreeNode child)
                        {
                            yield return child;
                        }
                    }

                    continue;
            }
        }
    }

    private static bool IsAstAliasProperty(Type ownerType, string propertyName)
    {
        return ownerType.Name == "IfStatement" &&
               (propertyName == "ThenBranch" || propertyName == "ElseBranch") ||
               ownerType.Name == "ReturnStatement" && propertyName == "Expression";
    }
}
