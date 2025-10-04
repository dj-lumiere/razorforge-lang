namespace Compilers.Shared.Lexer;

/// <summary>
/// Exception thrown when the lexical analyzer encounters invalid input.
/// Used for reporting syntax errors during the tokenization phase.
/// </summary>
/// <remarks>
/// LexerExceptions typically occur when:
/// <list type="bullet">
/// <item>Invalid character sequences are encountered</item>
/// <item>Unterminated string or character literals</item>
/// <item>Invalid escape sequences</item>
/// <item>Unknown numeric suffixes</item>
/// <item>Invalid Unicode escape codes</item>
/// </list>
/// The exception message should provide clear, actionable error information
/// including line numbers and character positions when possible.
/// </remarks>
public class LexerException : Exception
{
    /// <summary>
    /// Initializes a new instance of the LexerException entity with a specified error message.
    /// </summary>
    /// <param name="message">The error message that explains the reason for the exception</param>
    public LexerException(string message) : base(message)
    {
    }
}