namespace Compilers.Shared.Errors;

/// <summary>
/// Error thrown during lexical analysis (tokenization).
/// </summary>
public class LexerError : CompilerError
{
    public LexerError(string code, string message, SourceSpan span) : base(code: code,
        message: message,
        span: span)
    {
    }

    public static LexerError UnterminatedString(string? file, int line, int column, int position)
    {
        return new LexerError(code: ErrorCode.UnterminatedString,
            message: "Unterminated string literal",
            span: SourceSpan.Point(file: file,
                line: line,
                column: column,
                position: position)) { Hint = "Add a closing '\"' to terminate the string" };
    }

    public static LexerError UnterminatedCharacter(string? file, int line, int column,
        int position)
    {
        return new LexerError(code: ErrorCode.UnterminatedCharacter,
            message: "Unterminated character literal",
            span: SourceSpan.Point(file: file,
                line: line,
                column: column,
                position: position))
        {
            Hint = "Add a closing \"'\" to terminate the character literal"
        };
    }

    public static LexerError InvalidEscapeSequence(string escape, string? file, int line,
        int column, int position)
    {
        return new LexerError(code: ErrorCode.InvalidEscapeSequence,
            message: $"Invalid escape sequence '\\{escape}'",
            span: SourceSpan.Point(file: file,
                line: line,
                column: column,
                position: position))
        {
            Hint = "Valid escape sequences are: \\n, \\t, \\r, \\\\, \\\", \\', \\0, \\uXXXX"
        };
    }

    public static LexerError UnknownSuffix(string suffix, string? file, int line, int column,
        int position)
    {
        return new LexerError(code: ErrorCode.UnknownSuffix,
            message: $"Unknown numeric suffix '{suffix}'",
            span: SourceSpan.Point(file: file,
                line: line,
                column: column,
                position: position))
        {
            Hint = "Valid suffixes include: s8, s16, s32, s64, u8, u16, u32, u64, f32, f64"
        };
    }

    public static LexerError InvalidUnicodeEscape(int expected, string? file, int line, int column,
        int position)
    {
        return new LexerError(code: ErrorCode.InvalidUnicodeEscape,
            message: $"Invalid Unicode escape sequence: expected {expected} hex digits",
            span: SourceSpan.Point(file: file,
                line: line,
                column: column,
                position: position))
        {
            Hint = $"Unicode escapes must have exactly {expected} hexadecimal digits"
        };
    }

    public static LexerError UnexpectedCharacter(char c, string? file, int line, int column,
        int position)
    {
        string charDisplay = char.IsControl(c: c)
            ? $"U+{(int)c:X4}"
            : $"'{c}'";
        return new LexerError(code: ErrorCode.UnexpectedCharacter,
            message: $"Unexpected character {charDisplay}",
            span: SourceSpan.Point(file: file,
                line: line,
                column: column,
                position: position));
    }

    public static LexerError UnnecessarySyntax(string description, string? file, int line,
        int column, int position)
    {
        return new LexerError(code: ErrorCode.UnnecessarySyntax,
            message: description,
            span: SourceSpan.Point(file: file,
                line: line,
                column: column,
                position: position));
    }
}
