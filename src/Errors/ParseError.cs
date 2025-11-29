namespace Compilers.Shared.Errors;

/// <summary>
/// Error thrown during parsing (syntax analysis).
/// </summary>
public class ParseError : CompilerError
{
    public ParseError(string code, string message, SourceSpan span)
        : base(code: code, message: message, span: span)
    {
    }

    public static ParseError UnexpectedToken(string found, string? expected, string? file, int line, int column, int position)
    {
        string message = expected != null
            ? $"Unexpected token '{found}', expected {expected}"
            : $"Unexpected token '{found}'";

        return new ParseError(
            code: ErrorCode.UnexpectedToken,
            message: message,
            span: SourceSpan.Point(file: file, line: line, column: column, position: position));
    }

    public static ParseError ExpectedToken(string expected, string found, string? file, int line, int column, int position)
    {
        return new ParseError(
            code: ErrorCode.ExpectedToken,
            message: $"Expected {expected}, but found '{found}'",
            span: SourceSpan.Point(file: file, line: line, column: column, position: position));
    }

    public static ParseError ExpectedExpression(string found, string? file, int line, int column, int position)
    {
        return new ParseError(
            code: ErrorCode.ExpectedExpression,
            message: $"Expected expression, but found '{found}'",
            span: SourceSpan.Point(file: file, line: line, column: column, position: position));
    }

    public static ParseError ExpectedStatement(string found, string? file, int line, int column, int position)
    {
        return new ParseError(
            code: ErrorCode.ExpectedStatement,
            message: $"Expected statement, but found '{found}'",
            span: SourceSpan.Point(file: file, line: line, column: column, position: position));
    }

    public static ParseError ExpectedType(string found, string? file, int line, int column, int position)
    {
        return new ParseError(
            code: ErrorCode.ExpectedType,
            message: $"Expected type, but found '{found}'",
            span: SourceSpan.Point(file: file, line: line, column: column, position: position));
    }

    public static ParseError ExpectedIdentifier(string found, string? file, int line, int column, int position)
    {
        return new ParseError(
            code: ErrorCode.ExpectedIdentifier,
            message: $"Expected identifier, but found '{found}'",
            span: SourceSpan.Point(file: file, line: line, column: column, position: position));
    }

    public static ParseError ExpectedPattern(string found, string? file, int line, int column, int position)
    {
        return new ParseError(
            code: ErrorCode.ExpectedPattern,
            message: $"Expected pattern, but found '{found}'",
            span: SourceSpan.Point(file: file, line: line, column: column, position: position));
    }

    public static ParseError InvalidLiteral(string type, string value, string? file, int line, int column, int position)
    {
        return new ParseError(
            code: ErrorCode.InvalidLiteral,
            message: $"Invalid {type} literal: '{value}'",
            span: SourceSpan.Point(file: file, line: line, column: column, position: position));
    }

    public static ParseError IndentationError(string message, string? file, int line, int column, int position)
    {
        return new ParseError(
            code: ErrorCode.IndentationError,
            message: message,
            span: SourceSpan.Point(file: file, line: line, column: column, position: position));
    }
}
