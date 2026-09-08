using Compiler.Diagnostics;
using SyntaxTree;

namespace Compiler.Verification.Results;

/// <summary>
/// Represents a semantic error during analysis.
/// </summary>
/// <param name="Code">The diagnostic code for this error.</param>
/// <param name="Message">The error message.</param>
/// <param name="Location">The source location of the error.</param>
public sealed record SemanticError(
    SemanticDiagnosticCode Code,
    string Message,
    SourceLocation Location)
{
    /// <summary>
    /// Gets the formatted error message including diagnostic code and location.
    /// Format: error[RF-S###]: filename:line:column: message
    /// </summary>
    public string FormattedMessage =>
        $"error[{Code.ToCodeString()}]: {Location.FileName}:{Location.Line}:{Location.Column}: {Message}";
}
