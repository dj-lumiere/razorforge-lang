namespace Compilers.Shared.Errors;

/// <summary>
/// Error thrown during code generation.
/// </summary>
public class CodeGenError : CompilerError
{
    public CodeGenError(string code, string message, SourceSpan span) : base(code: code,
        message: message,
        span: span)
    {
    }

    public static CodeGenError UnsupportedFeature(string feature, string? file, int line,
        int column, int position)
    {
        return new CodeGenError(code: ErrorCode.UnsupportedFeature,
            message: $"Unsupported feature: {feature}",
            span: SourceSpan.Point(file: file,
                line: line,
                column: column,
                position: position));
    }

    public static CodeGenError InternalError(string message, string? file, int line, int column,
        int position)
    {
        return new CodeGenError(code: ErrorCode.InternalError,
            message: $"Internal compiler error: {message}",
            span: SourceSpan.Point(file: file,
                line: line,
                column: column,
                position: position));
    }

    public static CodeGenError TargetError(string target, string message, string? file, int line,
        int column, int position)
    {
        return new CodeGenError(code: ErrorCode.TargetError,
            message: $"Target '{target}' error: {message}",
            span: SourceSpan.Point(file: file,
                line: line,
                column: column,
                position: position));
    }

    public static CodeGenError TypeResolutionFailed(string typeName, string context, string? file,
        int line, int column, int position)
    {
        return new CodeGenError(code: ErrorCode.TypeResolutionFailed,
            message: $"Failed to resolve type '{typeName}' during code generation: {context}",
            span: SourceSpan.Point(file: file,
                line: line,
                column: column,
                position: position));
    }

    public static CodeGenError GenericTypeRequiresArguments(string typeName, string? file, int line,
        int column, int position)
    {
        return new CodeGenError(code: ErrorCode.TypeResolutionFailed,
            message: $"Generic type '{typeName}' requires type arguments (e.g., '{typeName}<T>'). " +
                     $"Cannot use generic template directly without instantiation.",
            span: SourceSpan.Point(file: file,
                line: line,
                column: column,
                position: position));
    }

    public static CodeGenError UnknownType(string typeName, string? file, int line, int column,
        int position)
    {
        return new CodeGenError(code: ErrorCode.TypeResolutionFailed,
            message: $"Unknown type '{typeName}' - not a registered record, entity, or primitive type",
            span: SourceSpan.Point(file: file,
                line: line,
                column: column,
                position: position));
    }
}