namespace TypeModel.Reprs;

/// <summary>
/// Distinguishes pointer-like backend representations that all erase to LLVM <c>ptr</c>.
/// </summary>
public enum PointerFlavor
{
    /// <summary>Not a pointer (non-pointer scalar).</summary>
    None,

    /// <summary>Heap entity pointer.</summary>
    Entity,

    /// <summary>Protocol witness/vtable pointer.</summary>
    Protocol,

    /// <summary>FreeRoutine pointer.</summary>
    Routine,

    /// <summary>Viewing borrow (Viewing[T]).</summary>
    Viewing,

    /// <summary>Modifying borrow (Modifying[T]).</summary>
    Modifying,

    /// <summary>Consulting borrow (read-only, internal use).</summary>
    Consulting,

    /// <summary>Amending borrow (exclusive, internal use).</summary>
    Amending,

    /// <summary>Reference-counted shared pointer (Retained[T]).</summary>
    Retained,

    /// <summary>Tracked ownership pointer (Tracked[T]).</summary>
    Tracked,

    /// <summary>Guarded ownership pointer (deferred concurrency use).</summary>
    Guarded,

    /// <summary>Witnessed wrapper pointer (marker-protocol conformance).</summary>
    Witnessed,

    /// <summary>Unsafe hijack borrow (Hijacked[T]).</summary>
    Hijacked,

    /// <summary>Untyped raw pointer (CPtr / Address).</summary>
    Raw
}
