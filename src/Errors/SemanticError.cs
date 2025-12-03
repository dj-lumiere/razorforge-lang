namespace Compilers.Shared.Errors;

/// <summary>
/// Error thrown during semantic analysis (type checking, name resolution, etc.).
/// </summary>
public class SemanticError : CompilerError
{
    public SemanticError(string code, string message, SourceSpan span) : base(code: code,
        message: message,
        span: span)
    {
    }

    public static SemanticError UndefinedVariable(string name, string? file, int line, int column,
        int position)
    {
        return new SemanticError(code: ErrorCode.UndefinedVariable,
            message: $"Undefined variable '{name}'",
            span: SourceSpan.Point(file: file,
                line: line,
                column: column,
                position: position));
    }

    public static SemanticError UndefinedFunction(string name, string? file, int line, int column,
        int position)
    {
        return new SemanticError(code: ErrorCode.UndefinedFunction,
            message: $"Undefined function '{name}'",
            span: SourceSpan.Point(file: file,
                line: line,
                column: column,
                position: position));
    }

    public static SemanticError UndefinedType(string name, string? file, int line, int column,
        int position)
    {
        return new SemanticError(code: ErrorCode.UndefinedType,
            message: $"Undefined type '{name}'",
            span: SourceSpan.Point(file: file,
                line: line,
                column: column,
                position: position));
    }

    public static SemanticError TypeMismatch(string expected, string actual, string? file,
        int line, int column, int position)
    {
        return new SemanticError(code: ErrorCode.TypeMismatch,
            message: $"Type mismatch: expected '{expected}', but found '{actual}'",
            span: SourceSpan.Point(file: file,
                line: line,
                column: column,
                position: position));
    }

    public static SemanticError DuplicateDefinition(string kind, string name, string? file,
        int line, int column, int position)
    {
        return new SemanticError(code: ErrorCode.DuplicateDefinition,
            message: $"Duplicate {kind} definition: '{name}'",
            span: SourceSpan.Point(file: file,
                line: line,
                column: column,
                position: position));
    }

    public static SemanticError InvalidOperation(string operation, string type, string? file,
        int line, int column, int position)
    {
        return new SemanticError(code: ErrorCode.InvalidOperation,
            message: $"Invalid operation '{operation}' for type '{type}'",
            span: SourceSpan.Point(file: file,
                line: line,
                column: column,
                position: position));
    }

    public static SemanticError MissingReturn(string function, string? file, int line, int column,
        int position)
    {
        return new SemanticError(code: ErrorCode.MissingReturn,
            message: $"Function '{function}' must return a value on all paths",
            span: SourceSpan.Point(file: file,
                line: line,
                column: column,
                position: position));
    }

    public static SemanticError InvalidArguments(string function, int expected, int actual,
        string? file, int line, int column, int position)
    {
        return new SemanticError(code: ErrorCode.InvalidArguments,
            message:
            $"Function '{function}' expects {expected} argument(s), but {actual} were provided",
            span: SourceSpan.Point(file: file,
                line: line,
                column: column,
                position: position));
    }

    public static SemanticError CircularDependency(string path, string? file, int line, int column,
        int position)
    {
        return new SemanticError(code: ErrorCode.CircularDependency,
            message: $"Circular dependency detected: {path}",
            span: SourceSpan.Point(file: file,
                line: line,
                column: column,
                position: position));
    }

    public static SemanticError ModuleNotFound(string modulePath, string? file, int line,
        int column, int position)
    {
        return new SemanticError(code: ErrorCode.ModuleNotFound,
            message: $"Module not found: '{modulePath}'",
            span: SourceSpan.Point(file: file,
                line: line,
                column: column,
                position: position));
    }

    public static SemanticError MemoryError(string message, string? file, int line, int column,
        int position)
    {
        return new SemanticError(code: ErrorCode.MemoryError,
            message: message,
            span: SourceSpan.Point(file: file,
                line: line,
                column: column,
                position: position));
    }
}
