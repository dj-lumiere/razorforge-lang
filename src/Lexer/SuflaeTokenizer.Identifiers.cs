namespace Compilers.Suflae.Lexer;

using Compilers.Shared.Lexer;

/// <summary>
/// Partial class containing identifier, keyword, and comment scanning methods for the Suflae tokenizer.
/// </summary>
/// <remarks>
/// <para>
/// Identifiers in Suflae follow the same rules as RazorForge:
/// </para>
/// <list type="bullet">
///   <item><description>Must start with a letter or underscore</description></item>
///   <item><description>Can contain letters, digits, and underscores</description></item>
///   <item><description>Optional single ? suffix for failable types (e.g., Integer?)</description></item>
/// </list>
/// <para>
/// The parser determines from context whether an identifier refers to a type or a value.
/// </para>
/// <para>
/// Note: Only a single ? is consumed as part of an identifier. The ?? operator
/// (none coalescing) is handled separately in the main scanner.
/// </para>
/// <para>
/// This file also handles script mode detection by tracking definition keywords.
/// </para>
/// </remarks>
public partial class SuflaeTokenizer
{
    #region Identifier Scanning

    /// <summary>
    /// Scans an identifier or keyword from the current position.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This method sets <see cref="_hasTokenOnLine"/> to true, which affects
    /// newline significance detection.
    /// </para>
    /// <para>
    /// Definition keywords (routine, entity, record, choice, variant, protocol) are
    /// tracked to determine whether the file is in script mode.
    /// </para>
    /// <para>
    /// After the base identifier, an optional single ? suffix is consumed for
    /// failable types (e.g., Integer?). Double ?? is NOT consumed here as
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
        _hasTokenOnLine = true;

        // Consume identifier characters
        while (IsIdentifierPart(Peek()))
            Advance();

        string text = _source.Substring(_tokenStart, _position - _tokenStart);

        // Check for optional ? suffix for failable types (e.g., Integer?)
        // Only consume single ? - double ?? is the none coalescing operator
        if (Peek() == '?' && Peek(1) != '?')
        {
            Advance();
            text += "?";
        }

        // Check if it's a keyword
        if (_keywords.TryGetValue(text, out TokenType type))
        {
            AddToken(type, text);

            // Track definition keywords for script mode detection
            if (type is TokenType.Routine or TokenType.Entity or TokenType.Record
                or TokenType.Choice or TokenType.Protocol)
            {
                _hasDefinitions = true;
            }
            return;
        }

        // Skip empty identifiers (defensive)
        if (string.IsNullOrEmpty(text))
            return;

        // Always emit Identifier - parser determines type vs value from context
        AddToken(TokenType.Identifier, text);
    }

    #endregion

    #region Comment Scanning

    /// <summary>
    /// Scans a comment from the current position.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Suflae uses the same comment syntax as RazorForge:
    /// </para>
    /// <list type="bullet">
    ///   <item><description># - Regular comment (ignored)</description></item>
    ///   <item><description>### - Documentation comment (tokenized)</description></item>
    /// </list>
    /// <para>
    /// All comments extend to the end of the line. The newline character
    /// is not consumed.
    /// </para>
    /// </remarks>
    private void ScanComment()
    {
        // Check for doc comment (###)
        if (Peek() == '#' && Peek(1) == '#')
        {
            Advance(); // consume second #
            Advance(); // consume third #

            int start = _position;

            // Consume until end of line
            while (Peek() != '\n' && !IsAtEnd())
                Advance();

            string text = _source.Substring(start, _position - start);
            AddToken(TokenType.DocComment, text);
        }
        else
        {
            // Regular comment - consume until end of line (no token emitted)
            while (Peek() != '\n' && !IsAtEnd())
                Advance();
        }
    }

    #endregion
}
