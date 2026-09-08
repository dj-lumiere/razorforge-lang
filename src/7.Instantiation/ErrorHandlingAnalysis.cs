using TypeModel.Types;

namespace Compiler.Instantiation;

/// <summary>
/// Result of analyzing throw/absent keywords in a failable function body.
/// </summary>
public sealed class ErrorHandlingAnalysis
{
    /// <summary>Whether the body contains any throw statements.</summary>
    public bool HasThrow { get; set; }

    /// <summary>Whether the body contains any absent statements.</summary>
    public bool HasAbsent { get; set; }

    /// <summary>
    /// Concrete crashable types directly thrown in this body (from <c>throw</c> statements
    /// whose expression has a resolved type). Does not include types thrown by called routines.
    /// </summary>
    public HashSet<TypeInfo> ThrownTypes { get; } = [];
}
