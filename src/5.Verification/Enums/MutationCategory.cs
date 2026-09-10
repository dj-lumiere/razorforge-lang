namespace Builder.Verification.Enums;

/// <summary>
/// Mutation category for memberRoutines, inferred by the builder.
/// Determines what token types can call a memberRoutine.
/// </summary>
/// <remarks>
/// The builder automatically infers mutation categories using three-phase analysis:
///
/// Step 1 (Direct Analysis):
///   - If memberRoutine writes to any member variable of me -> Writable
///   - If memberRoutine calls .grasp() on me member variables -> Writable
///
/// Step 2 (Call Graph Propagation):
///   - If memberRoutine calls a Writable memberRoutine on me -> Writable
///   - If memberRoutine calls a Reshaping memberRoutine on me -> Reshaping
///   - Repeat until fixpoint (no changes)
///
/// Step 3 (Token Checking):
///   - Viewing/Consulting tokens can only call Readonly memberRoutines
///   - Modifying/Amending tokens can call Readonly or Writable memberRoutines
///   - Only owned/non-token access can call Reshaping memberRoutines
/// </remarks>
public enum MutationCategory
{
    /// <summary>
    /// Read-only access, doesn't mutate me.
    /// Works with all token types: Viewing, Modifying, Consulting, Amending.
    /// </summary>
    Readonly,

    /// <summary>
    /// Mutates in-place within existing memory allocation.
    /// Needs modifiable token: Modifying or Amending.
    /// Cannot be called through Viewing or Consulting tokens.
    /// </summary>
    Writable,

    /// <summary>
    /// Can relocate memory buffers (e.g., List.push causing reallocation).
    /// Banned during iteration to prevent iterator invalidation (RF-S625).
    /// Needs ownership or exclusive access outside iteration.
    /// The MOST restrictive category for callers — it must be claimed EXPLICITLY (@reshaping); it is
    /// NOT a default. (It once was the field default on RoutineInfo, which silently made any
    /// unset-category routine look maximally-mutating — a latent bug.)
    /// </summary>
    Reshaping
}

/// <summary>The ONE place a routine's declared mutation category is derived from its source annotations —
/// shared by every routine-registration path (SignatureResolver, TypeBodyResolver, StdlibLoader) so they
/// cannot drift. "No annotation" = <see cref="MutationCategory.Writable"/>: a routine mutates unless
/// marked <c>@readonly</c>, and <c>@reshaping</c> is a stronger claim that must be explicit.</summary>
public static class MutationCategoryExtensions
{
    /// <summary>Maps <c>@readonly</c> → Readonly, <c>@reshaping</c> → Reshaping, anything else → Writable.</summary>
    public static MutationCategory FromAnnotations(ICollection<string>? annotations)
    {
        if (annotations is null)
        {
            return MutationCategory.Writable;
        }

        if (annotations.Contains(item: "readonly"))
        {
            return MutationCategory.Readonly;
        }

        if (annotations.Contains(item: "reshaping"))
        {
            return MutationCategory.Reshaping;
        }

        return MutationCategory.Writable;
    }
}
