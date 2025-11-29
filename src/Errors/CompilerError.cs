namespace Compilers.Shared.Errors;

/// <summary>
/// Severity level for compiler diagnostics.
/// </summary>
public enum DiagnosticSeverity
{
    /// <summary>Informational hint, not an error</summary>
    Hint,
    /// <summary>Warning that doesn't prevent compilation</summary>
    Warning,
    /// <summary>Error that prevents successful compilation</summary>
    Error,
    /// <summary>Fatal error that stops compilation immediately</summary>
    Fatal
}

/// <summary>
/// Error codes for categorizing compiler errors.
/// Format: E[Phase][Number] where Phase is L=Lexer, P=Parser, S=Semantic, G=CodeGen
/// </summary>
public static class ErrorCode
{
    // Lexer errors (EL001-EL099)
    public const string UnterminatedString = "EL001";
    public const string UnterminatedCharacter = "EL002";
    public const string InvalidEscapeSequence = "EL003";
    public const string InvalidNumericLiteral = "EL004";
    public const string UnknownSuffix = "EL005";
    public const string InvalidUnicodeEscape = "EL006";
    public const string UnexpectedCharacter = "EL007";
    public const string UnnecessarySyntax = "EL008";  // TimeParadoxError

    // Parser errors (EP001-EP099)
    public const string UnexpectedToken = "EP001";
    public const string ExpectedToken = "EP002";
    public const string ExpectedExpression = "EP003";
    public const string ExpectedStatement = "EP004";
    public const string ExpectedDeclaration = "EP005";
    public const string ExpectedType = "EP006";
    public const string ExpectedIdentifier = "EP007";
    public const string ExpectedPattern = "EP008";
    public const string InvalidLiteral = "EP009";
    public const string IndentationError = "EP010";

    // Semantic errors (ES001-ES099)
    public const string UndefinedVariable = "ES001";
    public const string UndefinedFunction = "ES002";
    public const string UndefinedType = "ES003";
    public const string TypeMismatch = "ES004";
    public const string DuplicateDefinition = "ES005";
    public const string InvalidOperation = "ES006";
    public const string MissingReturn = "ES007";
    public const string InvalidArguments = "ES008";
    public const string CircularDependency = "ES009";
    public const string ModuleNotFound = "ES010";
    public const string AccessViolation = "ES011";
    public const string MemoryError = "ES012";

    // CodeGen errors (EG001-EG099)
    public const string UnsupportedFeature = "EG001";
    public const string InternalError = "EG002";
    public const string TargetError = "EG003";
}

/// <summary>
/// Base class for all compiler errors with rich source location and stack trace support.
/// Provides detailed error information including file, line, column, and contextual stack trace.
/// </summary>
public class CompilerError : Exception
{
    /// <summary>The error code for categorization (e.g., "EP001")</summary>
    public string Code { get; }

    /// <summary>The primary source location where the error occurred</summary>
    public SourceSpan Span { get; }

    /// <summary>The severity level of this diagnostic</summary>
    public DiagnosticSeverity Severity { get; }

    /// <summary>A short, helpful message describing the error</summary>
    public string ErrorMessage { get; }

    /// <summary>Optional hint on how to fix the error</summary>
    public string? Hint { get; init; }

    /// <summary>Optional related source locations (e.g., previous definition for duplicates)</summary>
    public List<(string Label, SourceSpan Span)> RelatedLocations { get; } = [];

    /// <summary>The compiler's logical call stack showing context</summary>
    public List<CompilerStackFrame> CompilerStack { get; } = [];

    /// <summary>The source code text (for context display)</summary>
    public string? SourceText { get; init; }

    public CompilerError(
        string code,
        string message,
        SourceSpan span,
        DiagnosticSeverity severity = DiagnosticSeverity.Error)
        : base(message: FormatMessage(code: code, message: message, span: span))
    {
        Code = code;
        ErrorMessage = message;
        Span = span;
        Severity = severity;
    }

    public CompilerError(
        string code,
        string message,
        SourceSpan span,
        Exception innerException,
        DiagnosticSeverity severity = DiagnosticSeverity.Error)
        : base(message: FormatMessage(code: code, message: message, span: span), innerException: innerException)
    {
        Code = code;
        ErrorMessage = message;
        Span = span;
        Severity = severity;
    }

    /// <summary>
    /// Adds a frame to the compiler stack trace.
    /// </summary>
    public CompilerError WithStackFrame(string description, SourceSpan span)
    {
        CompilerStack.Add(item: new CompilerStackFrame(Description: description, Span: span));
        return this;
    }

    /// <summary>
    /// Adds a related location with a label.
    /// </summary>
    public CompilerError WithRelatedLocation(string label, SourceSpan span)
    {
        RelatedLocations.Add(item: (label, span));
        return this;
    }

    private static string FormatMessage(string code, string message, SourceSpan span)
    {
        return $"[{code}] {span}: {message}";
    }
}
