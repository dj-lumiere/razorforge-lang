using TypeModel.Types;

namespace Builder.Instantiation;

/// <summary>
/// Result of error handling variant generation.
/// </summary>
public sealed class ErrorHandlingResult
{
    /// <summary>Empty result (no variants generated).</summary>
    public static readonly ErrorHandlingResult Empty = new() { Variants = [] };

    /// <summary>The generated variants.</summary>
    public List<GeneratedVariant> Variants { get; init; } = [];

    /// <summary>Error message if generation failed.</summary>
    public string? Error { get; init; }

    /// <summary>Whether the routine body contains throw statements.</summary>
    public bool HasThrow { get; init; }

    /// <summary>Whether the routine body contains absent statements.</summary>
    public bool HasAbsent { get; init; }

    /// <summary>
    /// Concrete crashable types directly thrown by this routine (from <c>throw</c> statements).
    /// Populated from <see cref="ErrorHandlingAnalysis.ThrownTypes"/>.
    /// </summary>
    public List<TypeSymbol> ThrownTypes { get; init; } = [];

    /// <summary>Whether generation was successful.</summary>
    public bool Success => Error == null;
}
