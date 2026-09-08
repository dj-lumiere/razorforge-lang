using TypeModel.Enums;

namespace Compiler.Diagnostics;

/// <summary>
/// Exception thrown for grammar (lexer/parser) errors in both RazorForge and Suflae.
/// Contains diagnostic code, message, source location, and language.
/// </summary>
/// <param name="code">The code.</param>
/// <param name="message">The message.</param>
/// <param name="fileName">The file name.</param>
/// <param name="line">The line.</param>
/// <param name="column">The column.</param>
/// <param name="language">The language.</param>
public class GrammarException(
    GrammarDiagnosticCode code,
    string message,
    string fileName,
    int line,
    int column,
    Language language) : Exception(message: FormatMessage(code: code,
    message: message,
    fileName: fileName,
    line: line,
    column: column,
    language: language))
{
    /// <summary>
    /// The diagnostic code for this error.
    /// </summary>
    public GrammarDiagnosticCode Code { get; } = code;

    /// <summary>
    /// The unformatted message text (without the <c>error[…]: file:line:col:</c> prefix),
    /// for callers that re-wrap this error in another diagnostic envelope.
    /// </summary>
    public string RawMessage { get; } = message;

    /// <summary>
    /// The source file where the error occurred.
    /// </summary>
    public string FileName { get; } = fileName;

    /// <summary>
    /// The 1-based line number where the error occurred.
    /// </summary>
    public int Line { get; } = line;

    /// <summary>
    /// The 1-based column number where the error occurred.
    /// </summary>
    public int Column { get; } = column;

    /// <summary>
    /// The language that produced this error.
    /// </summary>
    public Language Language { get; } = language;

    /// <summary>
    /// Formats the error message in the standard format:
    /// error[RF-G001]: filename.rf:10:5: message
    /// </summary>
    private static string FormatMessage(GrammarDiagnosticCode code, string message,
        string fileName, int line, int column,
        Language language)
    {
        string location = fileName;
        if (line > 0)
        {
            location += $":{line}";
            if (column > 0)
            {
                location += $":{column}";
            }
        }

        return $"error[{code.ToCodeString(language: language)}]: {location}: {message}";
    }
}
