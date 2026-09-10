namespace Builder.Verification.Enums;

/// <summary>
/// The kind of scope in the program structure.
/// </summary>
public enum ScopeKind
{
    /// <summary>Global/module level scope.</summary>
    Global,

    /// <summary>Module scope.</summary>
    Module,

    /// <summary>Type body scope (record, entity, etc.).</summary>
    Type,

    /// <summary>FreeRoutine/routine body scope.</summary>
    Function,

    /// <summary>Block scope (if, when, etc.).</summary>
    Block,

    /// <summary>Loop scope (while, for, until).</summary>
    Loop,

    /// <summary>Danger block scope (RazorForge only).</summary>
    Danger,

    /// <summary>Iteration scope - tracks active iterators for reshaping checking.</summary>
    Iteration
}
