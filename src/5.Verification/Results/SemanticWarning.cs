using Builder.Diagnostics;
using SyntaxTree;

namespace Builder.Verification.Results;

/// <summary>
/// Represents a semantic warning during analysis.
/// </summary>
/// <param name="Code">The diagnostic code for this warning.</param>
/// <param name="Message">The warning message.</param>
/// <param name="Location">The source location of the warning.</param>
public sealed record SemanticWarning(
    SemanticWarningCode Code,
    string Message,
    SourceLocation Location)
{
    /// <summary>
    /// Gets the formatted warning message including diagnostic code and location.
    /// Format: warning[RF-W###]: filename:line:column: message
    /// </summary>
    public string FormattedMessage =>
        $"warning[{Code.ToCodeString()}]: {Location.FileName}:{Location.Line}:{Location.Column}: {Message}";
}
