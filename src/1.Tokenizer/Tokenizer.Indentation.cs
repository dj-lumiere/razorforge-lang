using Compiler.Diagnostics;

namespace Compiler.Tokenizer;

/// <summary>
/// Partial class containing indentation and newline handling memberRoutines for the unified tokenizer.
/// </summary>
/// <remarks>
/// <para>
/// Both RazorForge and Suflae use Python-style significant indentation for block structure.
/// This file contains the logic for:
/// </para>
/// <list type="bullet">
///   <item><description>Measuring indentation at the start of each line</description></item>
///   <item><description>Emitting INDENT tokens when indentation increases</description></item>
///   <item><description>Emitting DEDENT tokens when indentation decreases</description></item>
///   <item><description>Determining which newlines are significant</description></item>
/// </list>
/// </remarks>
public partial class Tokenizer
{
    #region Indentation Handling

    /// <summary>
    /// Handles indentation at the start of a line.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This memberRoutine is called at the beginning of each line (when column == 1).
    /// It measures the leading whitespace and compares it to the current
    /// indentation level to determine whether INDENT or DEDENT tokens are needed.
    /// </para>
    /// <para>
    /// Indentation rules:
    /// <list type="bullet">
    ///   <item><description>Each indentation level is 2 spaces</description></item>
    ///   <item><description>Tabs are rejected; indentation must use spaces</description></item>
    ///   <item><description>Indentation must be a multiple of 2</description></item>
    ///   <item><description>Indentation increases start a new block</description></item>
    /// </list>
    /// </para>
    /// </remarks>
    /// <exception cref="GrammarException">
    /// Thrown when indentation is misaligned (not a multiple of 2).
    /// </exception>
    private void HandleIndentation()
    {
        int spaces = CountLeadingWhitespace();

        // Skip empty lines (don't change indentation state)
        // Note: Check for \r to handle CRLF line endings on Windows
        if (Peek() == '\n' || Peek() == '\r' || IsAtEnd())
        {
            return;
        }

        // Skip lines with only comments (don't change indentation state), with a doc-comment
        // block-opener/closer exception (see HandleCommentLineIndentation).
        if (Peek() == '#')
        {
            HandleCommentLineIndentation(spaces: spaces);
            return;
        }

        // Skip INDENT/DEDENT inside brackets (L21)
        if (_bracketDepth > 0)
        {
            return;
        }

        // Leading-operator line continuation: when the first token on this line is an
        // infix/continuation operator (`and`, `or`, `but`, arithmetic/comparison/bitwise
        // symbols, `.`, `??`), the line continues the previous logical line instead of
        // starting a new statement. Drop the newline that terminated the previous line
        // and skip INDENT/DEDENT for this line — no block boundary, no indent-stack change.
        //
        // Guard on `spaces >= current indent`: when this line is LESS indented than the
        // enclosing block, a genuine DEDENT takes priority (so a stray operator-led line
        // cannot silently swallow a block boundary).
        if (spaces >= _indentStack.Peek() && StartsWithContinuationOperator())
        {
            if (_tokens.Count > 0 && _tokens[^1].Type == TokenType.Newline)
            {
                _tokens.RemoveAt(index: _tokens.Count - 1);
            }
            return;
        }

        // Validate indentation alignment
        if (spaces % 2 != 0)
        {
            throw new GrammarException(code: GrammarDiagnosticCode.InconsistentIndentation,
                message: $"Indentation error: expected multiple of 2 spaces, got {spaces} spaces",
                fileName: _fileName,
                line: _line,
                column: _column,
                language: _language);
        }

        // Handle indentation increase (new block): ONE Indent per block regardless of space jump.
        // Using a stack of actual space counts ensures each Indent matches exactly one Dedent.
        if (spaces > _indentStack.Peek())
        {
            EmitIndent(spaces: spaces);
            return;
        }

        // Handle dedents when indentation decreases: pop until stack top matches current spaces.
        EmitDedents(spaces: spaces);
    }

    /// <summary>
    /// Counts the leading whitespace on the current line (tabs count as 2 spaces) and consumes it.
    /// </summary>
    /// <exception cref="GrammarException">Thrown when tabs and spaces are mixed.</exception>
    private int CountLeadingWhitespace()
    {
        int spaces = 0;
        bool hasSpaces = false;
        bool hasTabs = false;

        // Count leading whitespace
        while (Peek() == ' ' || Peek() == '\t')
        {
            if (Peek() == ' ')
            {
                spaces += 1;
                hasSpaces = true;
            }
            else // Tab counts as 2 spaces
            {
                spaces += 2;
                hasTabs = true;
            }

            Advance();
        }

        // Reject mixed tabs and spaces
        if (hasTabs && hasSpaces)
        {
            throw new GrammarException(code: GrammarDiagnosticCode.MixedTabsAndSpaces,
                message: "Indentation mixes tabs and spaces; use one or the other",
                fileName: _fileName,
                line: _line,
                column: _column,
                language: _language);
        }

        return spaces;
    }

    /// <summary>
    /// Handles indentation state for a comment-only line. Regular comments (# / ##) are inert.
    /// A doc comment (###) opens a new block when indentation increases (so the parser can enter
    /// bodies whose first content is a ### comment) and closes blocks when indentation decreases.
    /// </summary>
    private void HandleCommentLineIndentation(int spaces)
    {
        bool isDocComment = Peek(offset: 1) == '#' && Peek(offset: 2) == '#';
        if (isDocComment && spaces > _indentStack.Peek())
        {
            EmitIndent(spaces: spaces);
        }
        else if (isDocComment && spaces < _indentStack.Peek())
        {
            // A dedented doc comment CLOSES the blocks it dropped out of, so it attaches to the
            // OUTER declaration it precedes rather than being swallowed by the inner body it
            // visually trails (e.g. a `###` between a record body and the next `routine`). Regular
            // (# / ##) comments still never change indentation state — they are inert.
            EmitDedents(spaces: spaces);
        }
    }

    /// <summary>
    /// Emits an Indent token (preceded by a Newline if needed) and pushes the new indentation level.
    /// </summary>
    private void EmitIndent(int spaces)
    {
        // Ensure a Newline precedes the Indent token
        // (some tokens like > suppress newlines as continuation,
        //  but an indent always starts a new logical line)
        if (_tokens.Count == 0 || _tokens[^1].Type != TokenType.Newline)
        {
            AddToken(type: TokenType.Newline, text: "\\n");
        }

        AddToken(type: TokenType.Indent, text: "");
        _indentStack.Push(spaces);
    }

    /// <summary>
    /// Pops the indent stack and emits Dedent tokens until the stack top matches the current spaces.
    /// </summary>
    private void EmitDedents(int spaces)
    {
        while (spaces < _indentStack.Peek())
        {
            _indentStack.Pop();
            AddToken(type: TokenType.Dedent, text: "");
        }
    }

    /// <summary>
    /// Peeks (without consuming) whether the upcoming token on the current line is a
    /// word logical operator (<c>and</c>/<c>or</c>/<c>but</c>), indicating this line
    /// continues the previous one. Leading whitespace has already been consumed by the
    /// caller, so <c>Peek()</c> returns the first content character.
    /// </summary>
    /// <remarks>
    /// Only the WORD logical operators qualify. Symbolic operators (<c>==</c>, <c>&lt;</c>,
    /// <c>.</c>, …) are deliberately excluded: a <c>when</c> expression writes its arms as
    /// leading comparison/case patterns (<c>== 0x22 =&gt;</c>, <c>.RED =&gt;</c>), so
    /// treating a line-leading symbol as a continuation would swallow those arms. No valid
    /// statement or <c>when</c> arm begins with <c>and</c>/<c>or</c>/<c>but</c>, so these
    /// are unambiguous.
    /// </remarks>
    private bool StartsWithContinuationOperator()
    {
        // Require a trailing word boundary so identifiers like `orange`, `android`, or
        // `button` are not misread as `or`/`and`/`but`.
        return Peek() switch
        {
            'a' => MatchesKeywordAhead(word: "and"),
            'o' => MatchesKeywordAhead(word: "or"),
            'b' => MatchesKeywordAhead(word: "but"),
            _ => false
        };
    }

    /// <summary>
    /// Peeks whether the characters at the current position spell <paramref name="word"/>
    /// followed by a word boundary (a non-identifier character), without consuming input.
    /// </summary>
    /// <param name="word">The keyword to test for.</param>
    private bool MatchesKeywordAhead(string word)
    {
        for (int i = 0; i < word.Length; i++)
        {
            if (Peek(offset: i) != word[index: i])
            {
                return false;
            }
        }

        return !IsIdentifierPart(c: Peek(offset: word.Length));
    }

    #endregion

    #region Newline Handling

    /// <summary>
    /// Handles a newline character, determining whether it's significant.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Newlines are significant when they terminate a statement.
    /// However, newlines are ignored after certain tokens that indicate
    /// continuation (like open parentheses or binary operators).
    /// </para>
    /// <para>
    /// This memberRoutine also resets the <see cref="_hasTokenOnLine"/> flag for
    /// the next line.
    /// </para>
    /// </remarks>
    private void HandleNewline()
    {
        // Suppress all newlines inside brackets (L21)
        if (_bracketDepth > 0)
        {
            _hasTokenOnLine = false;
            return;
        }

        bool isSignificant = IsNewlineSignificant();

        if (isSignificant)
        {
            AddToken(type: TokenType.Newline, text: "\\n");
        }

        _hasTokenOnLine = false;
    }

    /// <summary>
    /// Determines whether the current newline is significant (terminates a statement).
    /// </summary>
    /// <returns>
    /// <c>true</c> if the newline should produce a token; <c>false</c> if it should be ignored.
    /// </returns>
    /// <remarks>
    /// <para>
    /// A newline is NOT significant (ignored) when:
    /// <list type="bullet">
    ///   <item><description>The line was empty (no tokens yet)</description></item>
    ///   <item><description>The last token was an opening delimiter ((, [)</description></item>
    ///   <item><description>The last token was a comma or dot</description></item>
    ///   <item><description>The last token was a binary operator (+, -, *, /, etc.)</description></item>
    ///   <item><description>The last token was an arrow (->, =>)</description></item>
    ///   <item><description>The last token was already a newline</description></item>
    /// </list>
    /// </para>
    /// </remarks>
    private bool IsNewlineSignificant()
    {
        // No tokens on line = not significant
        if (!_hasTokenOnLine)
        {
            return false;
        }

        // No tokens at all = not significant
        if (_tokens.Count == 0)
        {
            return false;
        }

        TokenType lastToken = _tokens[^1].Type;

        return lastToken switch
        {
            // Opening delimiters - continuation expected
            TokenType.LeftParen => false,
            TokenType.LeftBracket => false,

            // Separators - continuation expected
            TokenType.Comma => false,
            TokenType.Dot => false,

            // Binary operators - continuation expected
            TokenType.Plus => false,
            TokenType.Minus => false,
            TokenType.Star => false,
            TokenType.Slash => false,
            TokenType.Equal => false,
            TokenType.Less => false,
            TokenType.Greater => false,
            TokenType.And => false,
            TokenType.Or => false,

            // Arrows - continuation expected
            TokenType.Arrow => false,
            TokenType.FatArrow => false,

            // Already a newline - don't duplicate
            TokenType.Newline => false,

            // Colon is significant (type annotations)
            TokenType.Colon => true,

            // Everything else is significant
            _ => true
        };
    }

    #endregion
}
