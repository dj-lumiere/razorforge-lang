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
            // A classifier-first constraint `needs <Kind> T` DECLARES its parameter as a generic type
            // parameter of the routine — the `needs` alternative to the bracket form `[T]`. Every classifier
            // kind counts (AnyType/RecordType/EntityType/ChoiceType/FlagsType/VariantType/TupleType/
            // RoutineType/RedirectType/Crashable/ConstGeneric), not just AnyType: the classifier-first
            // migration (`T is XType` → `XType T`) renamed the surface but left this recognizing only the
            // old `AnyType` (from `needs T is TypeName`), so `needs ChoiceType T` on a free routine like
            // `routine S32(from: T)` never declared `T` — the resolver then reported "Unknown type T" for
            // `T` in a type-argument position (e.g. `LLVM::reinterpret_bits[T, S32]`).
            // `obeys P` (capability), `T in [...]` (TypeEquality), and `everywhere` (owner `Me`) do NOT
            // declare a new parameter — they constrain one declared elsewhere (bracket, owner, or another
            // classifier constraint) — so they are skipped.
            if (constraint.ConstraintType is ConstraintKind.Obeys or ConstraintKind.TypeEquality
                or ConstraintKind.Everywhere)
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
