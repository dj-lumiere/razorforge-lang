namespace Compilers.Shared.Lexer;

/// <summary>
/// Exception thrown when unnecessary syntax elements are detected that violate
/// the language's clean syntax philosophy.
/// </summary>
/// <remarks>
/// TimeParadoxError occurs when:
/// <list type="bullet">
/// <item>Unnecessary semicolons are used in expression contexts</item>
/// <item>Redundant braces around single statements</item>
/// <item>C-style syntax patterns that conflict with the language design</item>
/// </list>
/// </remarks>
public class TimeParadoxError : Exception
{
    /// <summary>
    /// Initializes a new instance of the TimeParadoxError with a specified error message.
    /// </summary>
    /// <param name="message">The error message explaining the syntax violation</param>
    public TimeParadoxError(string message) : base(message: message)
    {
    }
}
