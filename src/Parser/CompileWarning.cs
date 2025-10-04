namespace Compilers.Shared.Parser;

/// <summary>
/// Represents a compile-time warning for style violations or deprecated patterns
/// </summary>
public class CompileWarning
{
    public string Message { get; }
    public int Line { get; }
    public int Column { get; }
    public WarningSeverity Severity { get; }
    public string WarningCode { get; }

    public CompileWarning(string message, int line, int column, WarningSeverity severity, string warningCode)
    {
        Message = message;
        Line = line;
        Column = column;
        Severity = severity;
        WarningCode = warningCode;
    }
}

public enum WarningSeverity
{
    Info,
    Warning,
    StyleViolation
}

/// <summary>
/// Standard warning codes for syntax style violations
/// </summary>
public static class WarningCodes
{
    public const string UnnecessarySemicolon = "RF001";
    public const string UnnecessaryBraces = "CK001";
    public const string CStyleSyntax = "ST001";
}