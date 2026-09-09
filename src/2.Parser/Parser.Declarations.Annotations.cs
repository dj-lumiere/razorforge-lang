using Compiler.Diagnostics;
using Compiler.Tokenizer;

namespace Compiler.Parser;

/// <summary>
/// Partial class containing annotation parsing helpers for declarations.
/// </summary>
public partial class Parser
{
    private List<string> ParseAnnotations()
    {
        var annotations = new List<string>();

        // Handle @annotation and @[...] compound annotations
        while (Check(type: TokenType.At))
        {
            if (!CheckAndAdvance(type: TokenType.At))
            {
                break; // No more annotations
            }

            // Check for compound annotation syntax: @[attr1, attr2, ...]
            if (CheckAndAdvance(type: TokenType.LeftBracket))
            {
                ParseCompoundAnnotation(annotations: annotations);
            }
            else
            {
                ParseRegularAnnotation(annotations: annotations);
            }

            // Skip newlines between annotations (allows multiple @attr on separate lines)
            while (CheckAndAdvance(type: TokenType.Newline))
            {
                // Skip newlines
            }
        }

        return annotations;
    }

    /// <summary>
    /// Parses the body of a compound annotation <c>@[attr1, attr2, ...]</c> (the opening <c>@[</c> is
    /// already consumed) and appends each name (with optional arguments) to <paramref name="annotations"/>.
    /// </summary>
    private void ParseCompoundAnnotation(List<string> annotations)
    {
        // Parse comma-separated list of annotation names
        do
        {
            string compoundAnnot = ConsumeIdentifier(
                errorMessage: "Expected annotation name in compound annotation");

            // Check for optional arguments on each annotation
            if (CheckAndAdvance(type: TokenType.LeftParen))
            {
                compoundAnnot += "(" + ParseAnnotationArgumentList() + ")";
            }

            annotations.Add(item: compoundAnnot);
        } while (CheckAndAdvance(type: TokenType.Comma));

        Consume(type: TokenType.RightBracket,
            errorMessage: "Expected ']' after compound annotations");
    }

    /// <summary>
    /// Parses a regular annotation <c>@identifier</c> (the <c>@</c> is already consumed) with optional
    /// arguments and appends it to <paramref name="annotations"/>.
    /// </summary>
    private void ParseRegularAnnotation(List<string> annotations)
    {
        // Regular annotation: @identifier
        string annotName = ConsumeIdentifier(errorMessage: "Expected annotation name after '@'");

        // Check for annotation arguments: @something("size_of") or @deprecated(message: "text")
        if (CheckAndAdvance(type: TokenType.LeftParen))
        {
            annotName += "(" + ParseAnnotationArgumentList() + ")";
        }

        annotations.Add(item: annotName);
    }

    /// <summary>
    /// Parses the argument list for an annotation (the content inside parentheses).
    /// </summary>
    /// <returns>String representation of the argument list.</returns>
    private string ParseAnnotationArgumentList()
    {
        var arguments = new List<string>();

        if (!Check(type: TokenType.RightParen))
        {
            do
            {
                // Check for named argument: name: value or name = value
                TokenType nextToken = PeekToken(offset: 1)
                   .Type;
                if (Check(type: TokenType.Identifier) &&
                    nextToken is TokenType.Colon or TokenType.Assign)
                {
                    string argName = ConsumeIdentifier(errorMessage: "Expected argument name");
                    // Accept both ':' and '=' as separators
                    if (!CheckAndAdvance(TokenType.Colon, TokenType.Assign))
                    {
                        throw ThrowParseError(code: GrammarDiagnosticCode.UnexpectedToken,
                            message: "Expected ':' or '=' after argument name");
                    }

                    string argValue = ParseAnnotationValue();
                    arguments.Add(item: $"{argName}={argValue}");
                }
                else
                {
                    // Positional argument (string literal, number, identifier)
                    arguments.Add(item: ParseAnnotationValue());
                }
            } while (CheckAndAdvance(type: TokenType.Comma));
        }

        Consume(type: TokenType.RightParen,
            errorMessage: "Expected ')' after annotation arguments");

        return string.Join(separator: ", ", values: arguments);
    }

    /// <summary>
    /// Parses a single annotation argument value (string, number, bool, or identifier).
    /// </summary>
    /// <returns>String representation of the annotation value.</returns>
    private string ParseAnnotationValue()
    {
        // Annotation values are limited to build-time constants:
        // string, number, bool, or identifier (for enums/presets)

        // String literal — keep it quoted so the stored annotation round-trips as `@llvm("i64")`
        // rather than `@llvm(i64)`. All consumers strip the wrapping quotes (ExtractLlvmAnnotation /
        // llvm_ir template extraction / SA all Trim or strip one pair).
        if (Check(TokenType.TextLiteral, TokenType.BytesLiteral))
        {
            return $"\"{Advance().Text}\"";
        }

        // Boolean literals
        if (CheckAndAdvance(type: TokenType.True))
        {
            return "true";
        }

        if (CheckAndAdvance(type: TokenType.False))
        {
            return "false";
        }

        // Numeric literals
        if (Check(TokenType.UndecidedInteger,
                TokenType.IntegerLiteral,
                TokenType.S8Literal,
                TokenType.S16Literal,
                TokenType.S32Literal,
                TokenType.S64Literal,
                TokenType.S128Literal,
                TokenType.S256Literal,
                TokenType.U8Literal,
                TokenType.U16Literal,
                TokenType.U32Literal,
                TokenType.U64Literal,
                TokenType.U128Literal,
                TokenType.U256Literal,
                TokenType.AddressLiteral))
        {
            return Advance()
               .Text;
        }

        // Identifier (for choice values or constant references)
        if (Check(type: TokenType.Identifier))
        {
            return Advance()
               .Text;
        }

        throw ThrowParseError(code: GrammarDiagnosticCode.ExpectedAnnotationValue,
            message: $"Expected annotation value, got {CurrentToken.Type}");
    }

}
