namespace Compilers.Shared.Parser;

/// <summary>
/// Represents a compile-time warning for style violations or deprecated patterns
/// </summary>
public class CompileWarning(string message, int line, int column, WarningSeverity severity, string warningCode)
{
    public string Message { get; } = message;
    public int Line { get; } = line;
    public int Column { get; } = column;
    public WarningSeverity Severity { get; } = severity;
    public string WarningCode { get; } = warningCode;
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
    public const string UnnecessaryBraces = "CK001";
    public const string CStyleSyntax = "ST001";
}
