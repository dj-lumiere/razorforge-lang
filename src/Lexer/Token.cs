namespace Compilers.Shared.Lexer;

/// <summary>
/// Represents a single token produced by the lexical analyzer.
/// Contains all information needed for parsing and error reporting.
/// </summary>
/// <param name="Type">The type of token (keyword, identifier, literal, etc.)</param>
/// <param name="Text">The actual text content from the source code</param>
/// <param name="Line">1-based line number where this token appears</param>
/// <param name="Column">1-based column number where this token starts</param>
/// <param name="Position">0-based absolute position in the source text (optional)</param>
/// <remarks>
/// Tokens are immutable records that preserve source location information
/// for accurate error reporting and IDE integration features like hover
/// tooltips and go-to-definition.
/// </remarks>
public record Token(TokenType Type, string Text, int Line, int Column, int Position = -1);