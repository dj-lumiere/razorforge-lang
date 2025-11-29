namespace Compilers.Shared.Errors;

/// <summary>
/// Represents a span of source code with start and end positions.
/// Used for precise error highlighting and source context display.
/// </summary>
/// <param name="File">The source file path (can be null for in-memory sources)</param>
/// <param name="StartLine">1-based starting line number</param>
/// <param name="StartColumn">1-based starting column number</param>
/// <param name="EndLine">1-based ending line number</param>
/// <param name="EndColumn">1-based ending column number</param>
/// <param name="StartPosition">0-based absolute start position in source</param>
/// <param name="EndPosition">0-based absolute end position in source</param>
public record SourceSpan(
    string? File,
    int StartLine,
    int StartColumn,
    int EndLine,
    int EndColumn,
    int StartPosition,
    int EndPosition)
{
    /// <summary>
    /// Creates a single-point span (for errors at a specific location).
    /// </summary>
    public static SourceSpan Point(string? file, int line, int column, int position)
    {
        return new SourceSpan(
            File: file,
            StartLine: line,
            StartColumn: column,
            EndLine: line,
            EndColumn: column,
            StartPosition: position,
            EndPosition: position);
    }

    /// <summary>
    /// Creates a span covering an entire line.
    /// </summary>
    public static SourceSpan Line(string? file, int line, int startColumn, int endColumn, int startPosition, int endPosition)
    {
        return new SourceSpan(
            File: file,
            StartLine: line,
            StartColumn: startColumn,
            EndLine: line,
            EndColumn: endColumn,
            StartPosition: startPosition,
            EndPosition: endPosition);
    }

    /// <summary>
    /// Returns a human-readable location string like "file.rf:10:5".
    /// </summary>
    public override string ToString()
    {
        string filePrefix = File != null ? $"{File}:" : "";
        if (StartLine == EndLine && StartColumn == EndColumn)
        {
            return $"{filePrefix}{StartLine}:{StartColumn}";
        }
        if (StartLine == EndLine)
        {
            return $"{filePrefix}{StartLine}:{StartColumn}-{EndColumn}";
        }
        return $"{filePrefix}{StartLine}:{StartColumn} to {EndLine}:{EndColumn}";
    }
}

/// <summary>
/// Represents a frame in the compiler's logical call stack.
/// Used to show the chain of constructs leading to an error.
/// </summary>
/// <param name="Description">Human-readable description of the frame (e.g., "in function 'foo'")</param>
/// <param name="Span">Source location of this frame</param>
public record CompilerStackFrame(string Description, SourceSpan Span)
{
    public override string ToString()
    {
        return $"  at {Description} ({Span})";
    }
}
