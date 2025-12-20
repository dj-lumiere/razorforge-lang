namespace Compilers.RazorForge.Lexer;

using Compilers.Shared.Lexer;

/// <summary>
/// Partial class containing identifier and keyword scanning methods for the RazorForge tokenizer.
/// </summary>
/// <remarks>
/// <para>
/// This file handles identifier recognition including:
/// <list type="bullet">
///   <item><description>Keywords (reserved words with special meaning)</description></item>
///   <item><description>Identifiers (all non-keyword names)</description></item>
///   <item><description>Optional ? suffix for failable types (e.g., s32?)</description></item>
/// </list>
/// </para>
/// <para>
/// Identifiers in RazorForge must start with a letter or underscore, and may contain
/// letters, digits, and underscores. The parser determines from context whether an
/// identifier refers to a type or a value.
/// </para>
/// <para>
/// Note: Only a single ? is consumed as part of an identifier. The ?? operator
/// (none coalescing) is handled separately in the main scanner.
/// </para>
/// </remarks>
public partial class RazorForgeTokenizer
{
    #region Identifier Scanning

    /// <summary>
    /// Scans an identifier or keyword from the current position.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This method is called after the first character of the identifier has been
    /// consumed by <see cref="ScanToken"/>. It continues scanning to build the
    /// complete identifier, then checks if it's a keyword.
    /// </para>
    /// <para>
    /// After the base identifier, an optional single ? suffix is consumed for
    /// failable types (e.g., s32?, Text?). Double ?? is NOT consumed here as
    /// it is the none coalescing operator.
    /// </para>
    /// <para>
    /// Token type determination:
    /// <list type="number">
    ///   <item><description>If the text matches a keyword, that keyword's token type is used.</description></item>
    ///   <item><description>Otherwise, it's an Identifier (parser determines if it's a type from context).</description></item>
    /// </list>
    /// </para>
    /// </remarks>
    private void ScanIdentifier()
    {
        // Consume identifier characters
        while (IsIdentifierPart(c: Peek()))
        {
            Advance();
        }

        string text = _source.Substring(startIndex: _tokenStart, length: _position - _tokenStart);

        // Check for optional ? suffix for failable types (e.g., s32?)
        // Only consume single ? - double ?? is the none coalescing operator
        if (Peek() == '?' && Peek(offset: 1) != '?')
        {
            Advance();
            text += "?";
        }

        // Check if it's a keyword
        if (_keywords.TryGetValue(key: text, value: out TokenType type))
        {
            AddToken(type: type, text: text);
            return;
        }

        // Skip empty identifiers (shouldn't happen, but defensive)
        if (string.IsNullOrEmpty(value: text))
        {
            return;
        }

        // Always emit Identifier - parser determines type vs value from context
        AddToken(type: TokenType.Identifier, text: text);
    }

    #endregion

    #region Comment Scanning

    /// <summary>
    /// Scans a comment from the current position.
    /// </summary>
    /// <remarks>
    /// <para>
    /// RazorForge supports two types of comments:
    /// <list type="bullet">
    ///   <item><description>Regular comments: # ... (ignored, not tokenized)</description></item>
    ///   <item><description>Documentation comments: ### ... (tokenized as DocComment)</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// The opening # has already been consumed when this method is called.
    /// It checks for additional # characters to determine if this is a doc comment.
    /// </para>
    /// <para>
    /// All comments extend to the end of the line. The newline character is not
    /// consumed (it will be handled on the next scan cycle).
    /// </para>
    /// </remarks>
    private void ScanComment()
    {
        // Check for doc comment (###)
        if (Peek() == '#' && Peek(offset: 1) == '#')
        {
            Advance(); // consume second #
            Advance(); // consume third #

            int start = _position;

            // Consume until end of line
            while (Peek() != '\n' && !IsAtEnd())
            {
                Advance();
            }

            string text = _source.Substring(startIndex: start, length: _position - start);
            AddToken(type: TokenType.DocComment, text: text);
        }
        else
        {
            // Regular comment - consume until end of line (no token emitted)
            while (Peek() != '\n' && !IsAtEnd())
            {
                Advance();
            }
        }
    }

    #endregion
}
