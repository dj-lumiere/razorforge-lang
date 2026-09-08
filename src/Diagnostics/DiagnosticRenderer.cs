using System.Collections.Concurrent;
using Compiler.Parser;
using SyntaxTree;
using Compiler.Verification.Results;

namespace Compiler.Diagnostics;

/// <summary>
/// Renders compiler diagnostics with a source-line excerpt and column caret, in the style of
/// modern compilers:
/// <code>
/// error[RF-S413]: playground\demo.rf:42:15: 'neighbors' cannot be directly assigned...
///     42 |   adj[i] = neighbors
///        |            ^
/// </code>
/// Every diagnostic family routes through here — semantic errors/warnings, grammar (parse)
/// errors, and parser build warnings — so the caret presentation is uniform.
/// Color uses <see cref="Console.ForegroundColor"/> (portable — no VT escape handling needed)
/// and is suppressed when the target stream is redirected or the NO_COLOR convention is set.
/// Source files are read lazily and cached per render batch; unreadable files degrade
/// gracefully to the header line only.
/// </summary>
public static class DiagnosticRenderer
{
    private static readonly ConcurrentDictionary<string, string[]?> _sourceCache =
        new(comparer: StringComparer.OrdinalIgnoreCase);

    private static readonly bool _colorEnvironment =
        Environment.GetEnvironmentVariable(variable: "NO_COLOR") == null
        && Environment.GetEnvironmentVariable(variable: "TERM") != "dumb";

    /// <summary>
    /// Cap on diagnostics rendered per batch: a cascade after one bad declaration can
    /// produce hundreds of follow-on errors, and nobody acts on more than the first
    /// screenful. The summary line still reports the true total.
    /// </summary>
    private const int MaxRenderedPerBatch = 20;

    /// <summary>Renders a semantic error (header + excerpt) to standard error.</summary>
    public static void Print(SemanticError error, string indent = "  ") =>
        PrintDiagnostic(writer: Console.Error, severity: "error", severityColor: ConsoleColor.Red,
            header: error.FormattedMessage, location: error.Location, indent: indent);

    /// <summary>Renders a semantic warning (header + excerpt) to standard error.</summary>
    public static void Print(SemanticWarning warning, string indent = "  ") =>
        PrintDiagnostic(writer: Console.Error, severity: "warning",
            severityColor: ConsoleColor.Yellow,
            header: warning.FormattedMessage, location: warning.Location, indent: indent);

    /// <summary>
    /// Renders a grammar (lexer/parser) error with excerpt + caret. The exception's message
    /// is already in standard <c>error[RF-G###]: file:line:col: …</c> form.
    /// Pass <paramref name="writer"/> to override the stream; defaults to standard error
    /// (diagnostics go to stderr, leaving stdout for program / requested output).
    /// </summary>
    public static void Print(GrammarException ex, TextWriter? writer = null, string indent = "")
    {
        PrintDiagnostic(writer: writer ?? Console.Error, severity: "error",
            severityColor: ConsoleColor.Red,
            header: ex.Message,
            location: new SourceLocation(FileName: ex.FileName, Line: ex.Line, Column: ex.Column,
                Position: 0),
            indent: indent);
    }

    /// <summary>Renders a parser build warning (style/deprecation) with excerpt + caret.</summary>
    public static void Print(BuildWarning warning, string indent = "  ")
    {
        string location = string.IsNullOrEmpty(value: warning.FileName)
            ? $"{warning.Line}:{warning.Column}"
            : $"{warning.FileName}:{warning.Line}:{warning.Column}";
        PrintDiagnostic(writer: Console.Error, severity: "warning",
            severityColor: ConsoleColor.Yellow,
            header: $"warning[{warning.WarningCode}]: {location}: {warning.Message}",
            location: new SourceLocation(FileName: warning.FileName, Line: warning.Line,
                Column: warning.Column, Position: 0),
            indent: indent);
    }

    /// <summary>Renders up to <see cref="MaxRenderedPerBatch"/> errors, then a suppression note.</summary>
    public static void PrintAll(IReadOnlyList<SemanticError> errors,
        string indent = "  ")
    {
        int shown = Math.Min(val1: errors.Count, val2: MaxRenderedPerBatch);
        for (int i = 0; i < shown; i++)
        {
            Print(error: errors[index: i], indent: indent);
        }

        if (errors.Count > shown)
        {
            Console.Error.WriteLine(
                value:
                $"{indent}... and {errors.Count - shown} more errors not shown. Fix the first batch and rebuild.");
        }
    }

    /// <summary>Renders up to <see cref="MaxRenderedPerBatch"/> warnings, then a suppression note.</summary>
    public static void PrintAll(IReadOnlyList<SemanticWarning> warnings,
        string indent = "  ")
    {
        int shown = Math.Min(val1: warnings.Count, val2: MaxRenderedPerBatch);
        for (int i = 0; i < shown; i++)
        {
            Print(warning: warnings[index: i], indent: indent);
        }

        if (warnings.Count > shown)
        {
            Console.Error.WriteLine(
                value: $"{indent}... and {warnings.Count - shown} more warnings not shown.");
        }
    }

    private static bool UseColorFor(TextWriter writer)
    {
        if (!_colorEnvironment)
        {
            return false;
        }

        return ReferenceEquals(objA: writer, objB: Console.Error)
            ? !Console.IsErrorRedirected
            : !Console.IsOutputRedirected;
    }

    private static void PrintDiagnostic(TextWriter writer, string severity,
        ConsoleColor severityColor, string header, SourceLocation location, string indent)
    {
        bool useColor = UseColorFor(writer: writer);

        // Header line: the severity word is colored; the rest stays default so the
        // file:line:col fragment remains terminal-clickable and copy-paste friendly.
        if (useColor && header.StartsWith(value: severity, comparisonType: StringComparison.Ordinal))
        {
            writer.Write(value: indent);
            ConsoleColor saved = Console.ForegroundColor;
            Console.ForegroundColor = severityColor;
            writer.Write(value: severity);
            Console.ForegroundColor = saved;
            writer.WriteLine(value: header[severity.Length..]);
        }
        else
        {
            writer.WriteLine(value: $"{indent}{header}");
        }

        PrintExcerpt(writer: writer, location: location, severityColor: severityColor,
            indent: indent, useColor: useColor);
    }

    /// <summary>
    /// Prints the offending source line with a caret under the diagnostic column.
    /// Silently prints nothing when the file is missing/unreadable or the location
    /// is out of range (e.g. synthesized nodes carry line 0).
    /// </summary>
    private static void PrintExcerpt(TextWriter writer, SourceLocation location,
        ConsoleColor severityColor, string indent, bool useColor)
    {
        if (string.IsNullOrEmpty(value: location.FileName) || location.Line <= 0)
        {
            return;
        }

        string[]? lines = _sourceCache.GetOrAdd(key: location.FileName, valueFactory: static path =>
        {
            try
            {
                return File.Exists(path: path) ? File.ReadAllLines(path: path) : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        });

        if (lines == null || location.Line > lines.Length)
        {
            return;
        }

        string sourceLine = lines[location.Line - 1];
        // Tabs would desynchronize the caret column; render them as single spaces
        // (RF style is space-indented, so this is a degenerate case anyway).
        sourceLine = sourceLine.Replace(oldChar: '\t', newChar: ' ');

        string lineNumber = location.Line.ToString();
        string gutterPad = new(c: ' ', count: lineNumber.Length);
        int caretOffset = Math.Clamp(value: location.Column - 1, min: 0, max: sourceLine.Length);

        WriteGutter(writer: writer, text: $"{indent}{lineNumber} | ", useColor: useColor);
        writer.WriteLine(value: sourceLine);
        WriteGutter(writer: writer, text: $"{indent}{gutterPad} | ", useColor: useColor);
        if (useColor)
        {
            ConsoleColor saved = Console.ForegroundColor;
            Console.ForegroundColor = severityColor;
            writer.WriteLine(value: $"{new string(c: ' ', count: caretOffset)}^");
            Console.ForegroundColor = saved;
        }
        else
        {
            writer.WriteLine(value: $"{new string(c: ' ', count: caretOffset)}^");
        }
    }

    private static void WriteGutter(TextWriter writer, string text, bool useColor)
    {
        if (useColor)
        {
            ConsoleColor saved = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.DarkGray;
            writer.Write(value: text);
            Console.ForegroundColor = saved;
        }
        else
        {
            writer.Write(value: text);
        }
    }
}
