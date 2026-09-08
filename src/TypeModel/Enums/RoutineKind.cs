namespace TypeModel.Enums;

/// <summary>
/// The kind of routine (function, member routine, creator, etc.).
/// </summary>
public enum RoutineKind
{
    /// <summary>Free-standing function (not attached to a type).</summary>
    FreeRoutine,

    /// <summary>Member routine (routine Type.name()).</summary>
    MemberRoutine,

    /// <summary>
    /// A routine that is defined by typewise (common routine Type.name())
    /// </summary>
    CommonRoutine,

    /// <summary>Creator (create).</summary>
    Creator,

    /// <summary>Anonymous lambda / closure expression.</summary>
    Lambda
}
