using System.Text;

namespace Compilers.Shared.Errors;

/// <summary>
/// Formats compiler errors with rich source context for display.
/// Provides Rust/Elm-style error messages with source snippets and caret pointers.
/// </summary>
public class ErrorFormatter
{
    /// <summary>
    /// Number of context lines to show before and after the error location.
    /// </summary>
    public int ContextLines { get; set; } = 2;

    /// <summary>
    /// Whether to use ANSI color codes in output.
    /// </summary>
    public bool UseColors { get; set; } = true;

    /// <summary>
    /// Formats a compiler error with source context.
    /// </summary>
    /// <param name="error">The compiler error to format</param>
    /// <param name="sourceText">The source text (optional, will use error.SourceText if available)</param>
    /// <returns>Formatted error message with source context</returns>
    public string Format(CompilerError error, string? sourceText = null)
    {
        var sb = new StringBuilder();
        string source = sourceText ?? error.SourceText ?? "";

        // Header: [E0001] error: message
        sb.Append(value: FormatHeader(error: error));
        sb.AppendLine();

        // Location: --> file:line:column
        sb.Append(value: "  --> ");
        sb.AppendLine(value: error.Span.ToString());

        // Source context with line numbers and caret
        if (!string.IsNullOrEmpty(value: source))
        {
            sb.Append(value: FormatSourceContext(source: source, span: error.Span));
        }

        // Hint (if available)
        if (!string.IsNullOrEmpty(value: error.Hint))
        {
            sb.AppendLine();
            sb.Append(value: FormatHint(hint: error.Hint));
        }

        // Related locations
        foreach ((string label, SourceSpan span) in error.RelatedLocations)
        {
            sb.AppendLine();
            sb.Append(value: $"  {label}: ");
            sb.AppendLine(value: span.ToString());
        }

        // Compiler stack trace
        if (error.CompilerStack.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(value: "Stack trace:");
            foreach (CompilerStackFrame frame in error.CompilerStack)
            {
                sb.AppendLine(value: frame.ToString());
            }
        }

        return sb.ToString();
    }

    private string FormatHeader(CompilerError error)
    {
        string severityStr = error.Severity switch
        {
            DiagnosticSeverity.Hint => "hint",
            DiagnosticSeverity.Warning => "warning",
            DiagnosticSeverity.Error => "error",
            DiagnosticSeverity.Fatal => "fatal",
            _ => "error"
        };

        if (UseColors)
        {
            string colorCode = error.Severity switch
            {
                DiagnosticSeverity.Hint => "\u001b[36m", // Cyan
                DiagnosticSeverity.Warning => "\u001b[33m", // Yellow
                DiagnosticSeverity.Error => "\u001b[31m", // Red
                DiagnosticSeverity.Fatal => "\u001b[91m", // Bright red
                _ => "\u001b[31m"
            };
            string reset = "\u001b[0m";
            string bold = "\u001b[1m";

            return
                $"{colorCode}{bold}[{error.Code}] {severityStr}{reset}: {bold}{error.ErrorMessage}{reset}";
        }

        return $"[{error.Code}] {severityStr}: {error.ErrorMessage}";
    }

    private string FormatSourceContext(string source, SourceSpan span)
    {
        var sb = new StringBuilder();
        string[] lines = source.Split(separator: '\n');

        int startLine = Math.Max(val1: 1, val2: span.StartLine - ContextLines);
        int endLine = Math.Min(val1: lines.Length, val2: span.EndLine + ContextLines);

        // Calculate gutter width (for line numbers)
        int gutterWidth = endLine.ToString()
                                 .Length;

        sb.AppendLine(value: $"   {new string(c: ' ', count: gutterWidth)}|");

        for (int i = startLine; i <= endLine; i++)
        {
            if (i - 1 >= lines.Length)
            {
                break;
            }

            string line = lines[i - 1]
               .TrimEnd(trimChar: '\r');
            string lineNum = i.ToString()
                              .PadLeft(totalWidth: gutterWidth);

            // Check if this is an error line
            bool isErrorLine = i >= span.StartLine && i <= span.EndLine;

            if (isErrorLine)
            {
                sb.AppendLine(value: $" {lineNum} | {line}");

                // Add caret indicator
                int caretStart = i == span.StartLine
                    ? span.StartColumn - 1
                    : 0;
                int caretEnd = i == span.EndLine
                    ? span.EndColumn - 1
                    : line.Length;
                caretEnd = Math.Max(val1: caretEnd, val2: caretStart + 1);

                string padding = new(c: ' ', count: caretStart);
                string carets = new(c: '^', count: Math.Max(val1: 1, val2: caretEnd - caretStart));

                string caretLine = $"   {new string(c: ' ', count: gutterWidth)}| {padding}";
                if (UseColors)
                {
                    sb.AppendLine(value: $"{caretLine}\u001b[31m{carets}\u001b[0m");
                }
                else
                {
                    sb.AppendLine(value: $"{caretLine}{carets}");
                }
            }
            else
            {
                sb.AppendLine(value: $" {lineNum} | {line}");
            }
        }

        return sb.ToString();
    }

    private string FormatHint(string hint)
    {
        if (UseColors)
        {
            return $"\u001b[36mhint\u001b[0m: {hint}";
        }

        return $"hint: {hint}";
    }

    /// <summary>
    /// Formats multiple errors, sorted by location.
    /// </summary>
    public string FormatAll(IEnumerable<CompilerError> errors, string? sourceText = null)
    {
        var sb = new StringBuilder();
        var sortedErrors = errors.OrderBy(keySelector: e => e.Span.File ?? "")
                                 .ThenBy(keySelector: e => e.Span.StartLine)
                                 .ThenBy(keySelector: e => e.Span.StartColumn)
                                 .ToList();

        foreach (CompilerError error in sortedErrors)
        {
            sb.AppendLine(value: Format(error: error, sourceText: sourceText));
        }

        // Summary
        int errorCount = sortedErrors.Count(predicate: e =>
            e.Severity == DiagnosticSeverity.Error || e.Severity == DiagnosticSeverity.Fatal);
        int warningCount =
            sortedErrors.Count(predicate: e => e.Severity == DiagnosticSeverity.Warning);

        if (errorCount > 0 || warningCount > 0)
        {
            sb.AppendLine();
            if (UseColors)
            {
                if (errorCount > 0)
                {
                    sb.Append(value: $"\u001b[31m{errorCount} error(s)\u001b[0m");
                }

                if (warningCount > 0)
                {
                    if (errorCount > 0)
                    {
                        sb.Append(value: ", ");
                    }

                    sb.Append(value: $"\u001b[33m{warningCount} warning(s)\u001b[0m");
                }
            }
            else
            {
                if (errorCount > 0)
                {
                    sb.Append(value: $"{errorCount} error(s)");
                }

                if (warningCount > 0)
                {
                    if (errorCount > 0)
                    {
                        sb.Append(value: ", ");
                    }

                    sb.Append(value: $"{warningCount} warning(s)");
                }
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }
}
