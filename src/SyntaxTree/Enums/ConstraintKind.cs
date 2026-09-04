namespace SyntaxTree;

/// <summary>
/// Types of generic constraints
/// </summary>
public enum ConstraintKind
{
    /// <summary>Protocol/interface implementation (T obeys Comparable)</summary>
    Obeys,

    /// <summary>Value type constraint (where T is RecordType)</summary>
    RecordType,

    /// <summary>Reference type constraint (where T is EntityType)</summary>
    EntityType,

    /// <summary>Routine/function type constraint (where T is RoutineType)</summary>
    RoutineType,

    /// <summary>Choice type constraint (where T is ChoiceType)</summary>
    ChoiceType,

    /// <summary>Flags type constraint (where T is FlagsType)</summary>
    FlagsType,

    /// <summary>Variant type constraint (where T is VariantType)</summary>
    VariantType,

    /// <summary>Tuple type constraint (where T is TupleType)</summary>
    TupleType,

    /// <summary>Zero-member-variable constraint (where T is ZeroMemvarType) — a type whose
    /// `allmemvarof` is empty: a field-less record, or a scalar kind (choice/flags) that carries no
    /// member variables. Lets a derive specialize the degenerate empty-field-walk case.</summary>
    ZeroMemvarType,

    /// <summary>Const generic type constraint (where N is Address)</summary>
    ConstGeneric,

    /// <summary>Type equality constraint (where T in [S32, U8])</summary>
    TypeEquality,

    /// <summary>Crashable type constraint (where T is Crashable).</summary>
    Crashable,

    /// <summary>Type-parameter DECLARATION (<c>needs T is TypeName</c>): declares the named identifier as
    /// an unconstrained generic type parameter of the routine/type — the explicit alternative to the
    /// bracket form <c>[T]</c>. Satisfied by ANY type. Used by the universal derive templates
    /// (<c>@overridable routine T.represent() needs T is TypeName</c>) so the placeholder <c>T</c> is a
    /// structurally-declared parameter (distinguishing a template from a concrete-type override) rather
    /// than an owner that merely happens to not resolve.</summary>
    AnyType,

    /// <summary>Standard-implementation eligibility constraint (<c>needs P everywhere</c>): the owner
    /// <c>Me</c> obeys protocol <c>P</c> IFF every member (allmemvarof/branchof/caseof, per kind) obeys it.
    /// The single ∀-quantified structural gate that drives a standard-impl template's eligibility and
    /// conformance verdict. ParameterName is <c>"Me"</c>; ConstraintTypes[0] is the protocol.</summary>
    Everywhere
}
