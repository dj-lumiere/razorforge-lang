using SyntaxTree;
using TypeModel.Enums;

namespace Builder.Declaration;

internal static class RoutineGenericParameters
{
    internal static void AddConstraintDeclarations(RoutineDeclaration routine)
    {
        if (routine.GenericConstraints is not { } constraints)
        {
            return;
        }

        foreach (GenericConstraintDeclaration constraint in constraints)
        {
            if (constraint.ConstraintType != ConstraintKind.AnyType)
            {
                continue;
            }

            routine.GenericParameters ??= [];
            if (!routine.GenericParameters.Contains(item: constraint.ParameterName))
            {
                routine.GenericParameters.Add(item: constraint.ParameterName);
            }
        }
    }
}
