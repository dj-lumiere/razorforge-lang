namespace Compiler.Tokenizer;

/// <summary>
/// Partial class containing identifier, keyword, and comment scanning memberRoutines for the unified tokenizer.
/// </summary>
/// <remarks>
/// <para>
/// Identifiers follow the same rules in both RazorForge and Suflae:
/// </para>
/// <list type="bullet">
///   <item><description>Must start with a letter, underscore, or dollar sign ($)</description></item>
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
public partial class Tokenizer
{
    #region Identifier Scanning

    /// <summary>
    /// Scans an identifier or keyword from the current position.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This memberRoutine sets <see cref="_hasTokenOnLine"/> to true, which affects
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

        // Comptime splice open '${' — a SEPARATE structural token distinct from a bare '$' wired
        // marker and a bare '{'. The main scan loop already consumed the '$' into _position, so
        // Peek() is the char right after it. Emit '${' as a SpliceOpen token and consume the '{';
        // the balanced closing '}' is an ordinary RightBrace matched by the parser. Guarded ahead
        // of the '$'-wired branch below so `${m.name}` never mis-tokenizes as `$` + identifier.
        if (_source[index: _tokenStart] == '$' && Peek() == '{')
        {
            Advance(); // consume '{'
            AddToken(type: TokenType.SpliceOpen, text: "${");
            return;
        }

        // Wired member-routine marker: a leading '$' emits its own Dollar token and re-anchors so
        // the bare identifier that follows scans on its own. Returns true when a lone '$' with no
        // identifier body was consumed (nothing more to emit).
        if (TryEmitWiredDollarMarker())
        {
            return;
        }

        // Consume identifier characters
        while (IsIdentifierPart(c: Peek()))
        {
            Advance();
        }

        string text = _source.Substring(startIndex: _tokenStart, length: _position - _tokenStart);

        // Check if text + "!" matches a keyword (for danger)
        if (Peek() == '!' && _keywords.TryGetValue(key: text + "!", value: out TokenType bangType))
        {
            Advance();
            AddToken(type: bangType, text: text + "!");
            _hasTokenOnLine = true;
            return;
        }

        // Check for special float/decimal literals: inf_fNN, nan_fNN, inf_dNN, nan_dNN
        if (TryMatchSpecialFloatLiteral(text: text, out TokenType specialType, out string specialBody))
        {
            AddToken(type: specialType, text: specialBody);
            return;
        }

        // Check if it's a keyword
        if (_keywords.TryGetValue(key: text, value: out TokenType type))
        {
            EmitKeywordToken(type: type, text: text);
            return;
        }

        // Skip empty identifiers (defensive)
        if (string.IsNullOrEmpty(value: text))
        {
            return;
        }

        // Always emit Identifier - parser determines type vs value from context
        AddToken(type: TokenType.Identifier, text: text);
    }

    /// <summary>
    /// Handles a leading '$' wired member-routine marker: emits the '$' as its own Dollar token and
    /// re-anchors the token start so the following bare identifier scans on its own. (The main scan
    /// loop already consumed the '$' into <see cref="_position"/>, so the identifier body is scanned
    /// by the caller's loop.)
    /// </summary>
    /// <returns>
    /// <c>true</c> if a lone '$' with no identifier body was consumed (caller should return);
    /// <c>false</c> otherwise (including when there was no '$' at all).
    /// </returns>
    private bool TryEmitWiredDollarMarker()
    {
        if (_source[index: _tokenStart] != '$')
        {
            return false;
        }

        _tokens.Add(item: new Token(Type: TokenType.Dollar, FileName: _fileName, Text: "$",
            Line: _tokenStartLine, Column: _tokenStartColumn, Position: _tokenStart));
        _tokenStart += 1;
        _tokenStartColumn += 1;
        // A lone '$' with no identifier body — nothing more to emit.
        return !IsIdentifierPart(c: Peek()) && _position == _tokenStart;
    }

    /// <summary>
    /// Emits a keyword token and tracks definition keywords for script mode detection.
    /// </summary>
    private void EmitKeywordToken(TokenType type, string text)
    {
        AddToken(type: type, text: text);

        // Track definition keywords for script mode detection
        if (type is TokenType.Routine or TokenType.Entity or TokenType.Record
            or TokenType.Choice or TokenType.Variant or TokenType.Flags or TokenType.Protocol)
        {
            _hasDefinitions = true;
        }
    }

    private static readonly Dictionary<string, TokenType> _specialFloatLiterals =
        new()
        {
            ["inf_f16"] = TokenType.F16Literal,
            ["nan_f16"] = TokenType.F16Literal,
            ["inf_f32"] = TokenType.F32Literal,
            ["nan_f32"] = TokenType.F32Literal,
            ["inf_f64"] = TokenType.F64Literal,
            ["nan_f64"] = TokenType.F64Literal,
            ["inf_f128"] = TokenType.F128Literal,
            ["nan_f128"] = TokenType.F128Literal,
            ["inf_d32"] = TokenType.D32Literal,
            ["nan_d32"] = TokenType.D32Literal,
            ["inf_d64"] = TokenType.D64Literal,
            ["nan_d64"] = TokenType.D64Literal,
            ["inf_d128"] = TokenType.D128Literal,
            ["nan_d128"] = TokenType.D128Literal,
        };

    private static bool TryMatchSpecialFloatLiteral(string text, out TokenType type, out string body)
    {
        if (_specialFloatLiterals.TryGetValue(key: text, value: out type))
        {
            body = text.StartsWith(value: "inf") ? "inf" : "nan";
            return true;
        }
        type = default;
        body = string.Empty;
        return false;
    }

    #endregion

    #region Comment Scanning

    /// <summary>
    /// Scans a comment from the current position.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both RazorForge and Suflae use the same comment syntax:
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
