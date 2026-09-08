namespace Compiler.Parser;

/// <summary>
/// Represents a build-time warning for style violations or deprecated patterns
/// </summary>
/// <param name="message">The message.</param>
/// <param name="line">The line.</param>
/// <param name="column">The column.</param>
/// <param name="severity">The severity.</param>
/// <param name="warningCode">The warning code.</param>
/// <param name="fileName">The source file the warning was raised in (empty when unknown).</param>
public class BuildWarning(
    string message,
    int line,
    int column,
    WarningSeverity severity,
    string warningCode,
    string fileName = "")
{
    /// <summary>Source file the warning was raised in (empty when unknown).</summary>
    public string FileName { get; } = fileName;

    /// <summary>Human-readable description of the warning condition.</summary>
    public string Message { get; } = message;

    /// <summary>Source line number (1-based) where the warning was detected.</summary>
    public int Line { get; } = line;

    /// <summary>Source column number (1-based) where the warning was detected.</summary>
    public int Column { get; } = column;

    /// <summary>The severity level of this warning.</summary>
    public WarningSeverity Severity { get; } = severity;

    /// <summary>The machine-readable warning code (e.g., "CK001") used to suppress or categorize this warning.</summary>
    public string WarningCode { get; } = warningCode;
}

/// <summary>
/// Severity levels for build-time warnings, determining how they are displayed and filtered.
/// </summary>
public enum WarningSeverity
{
    /// <summary>Informational message only; does not indicate a problem.</summary>
    Info,

    /// <summary>Potential issue that may lead to incorrect behavior or future errors.</summary>
    Warning,

    /// <summary>Code style violation that does not affect behavior but should be corrected.</summary>
    StyleViolation
}

/// <summary>
/// Standard warning codes for syntax style violations
/// </summary>
public static class WarningCodes
{
    /// <summary>Unnecessary braces in a context where indentation delimiters are expected (CK001).</summary>
    public const string UnnecessaryBraces = "CK001";

    /// <summary>C-style syntax used in a context where RazorForge/Suflae idioms are expected (ST001).</summary>
    public const string CStyleSyntax = "ST001";
}
