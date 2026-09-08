namespace TypeModel.Reprs;

/// <summary>
/// Backend-visible ABI/storage category for a fully resolved semantic type.
/// </summary>
public enum BackendReprKind
{
    /// <summary>No value (void / absent return).</summary>
    Void,
    /// <summary>Primitive scalar value (integer, float, bool, etc.).</summary>
    Scalar,
    /// <summary>Inline struct/record laid out as an LLVM aggregate.</summary>
    Aggregate,
    /// <summary>Heap-allocated entity passed by pointer.</summary>
    EntityRef,
    /// <summary>Protocol witness pointer (fat pointer or vtable ref).</summary>
    ProtocolRef,
    /// <summary>RC wrapper pointer (Retained/Viewing/etc.).</summary>
    WrapperRef,
    /// <summary>FreeRoutine pointer (routine reference).</summary>
    RoutineRef,
    /// <summary>Untyped raw pointer (CPtr / Address).</summary>
    RawPtr
}
