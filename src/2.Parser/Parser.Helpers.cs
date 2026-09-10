using Builder.Diagnostics;
using Builder.Tokenizer;
using SyntaxTree;

namespace Builder.Parser;

/// <summary>
/// Partial class containing helper memberRoutines: statement terminators,
/// identifier consumption, indentation handling, argument parsing, and collection literals.
/// </summary>
public partial class Parser
{
    #region Statement/Token Helpers

    /// <summary>
    /// Consumes statement terminators (newlines).
    /// Checks for unnecessary braces before accepting newline.
    /// </summary>
    private void ConsumeStatementTerminator()
    {
        // Check for unnecessary closing braces
        CheckUnnecessaryBrace();

        // Valid implicit terminators: DEDENT, else, elseif, EOF
        if (Check(type: TokenType.Dedent) || Check(type: TokenType.Else) ||
            Check(type: TokenType.Elseif) || IsAtEnd)
        {
            return;
        }

        // Optionally consume a newline if present
        CheckAndAdvance(type: TokenType.Newline);
    }

    /// <summary>
    /// Consumes an identifier token and returns its text.
    /// </summary>
    /// <param name="errorMessage">Error message to show if token is not an identifier.</param>
    /// <param name="allowKeywords">Whether contextual keywords may be consumed as identifiers.</param>
    /// <returns>The identifier text.</returns>
    /// <exception cref="GrammarException">Thrown if current token is not a valid identifier.</exception>
    private string ConsumeIdentifier(string errorMessage, bool allowKeywords = false)
    {
        if (CheckAndAdvance(type: TokenType.Identifier))
        {
            return PeekToken(offset: -1)
               .Text;
        }

        // Allow 'me' or 'Me' (Self tokens) as a valid identifier for memberRoutine parameters
        // 'me' is lowercase self reference, 'Me' is the type of self (for protocol memberRoutine signatures)
        if (CheckAndAdvance(TokenType.Me, TokenType.MyType))
        {
            return PeekToken(offset: -1)
               .Text;
        }

        // When allowKeywords is true, accept contextual keywords as identifiers
        // (e.g., 'from', 'to', 'by', 'step' as parameter names)
        if (allowKeywords && CurrentToken.Type != TokenType.Eof &&
            CurrentToken.Type != TokenType.Newline)
        {
            string text = CurrentToken.Text;
            Advance();
            return text;
        }

        Token current = CurrentToken;
        throw ThrowParseError(code: GrammarDiagnosticCode.ExpectedIdentifier,
            message: $"{errorMessage}. Expected Identifier, got {current.Type}.");
    }

    /// <summary>Returns true when <paramref name="type"/> is a keyword token also valid as a memberRoutine name (e.g. <c>none</c>).</summary>
    /// <param name="type">The token type to test.</param>
    private static bool IsKeywordValidAsMemberRoutineName(TokenType type)
    {
        return type == TokenType.NoneValue;
    }

    /// <summary>
    /// Set true while parsing a routine-declaration name when a wired marker (`$`) token is consumed
    /// on the memberRoutine segment. Read (and reset) by the routine-declaration parsers to populate
    /// <see cref="SyntaxTree.RoutineDeclaration.IsWiredMemberRoutine"/>. The `$` is NEVER folded into
    /// the decl name — the canonical name is the bare identifier.
    /// </summary>
    private bool _routineNameWired;

    private string ConsumeMemberRoutineName(string errorMessage)
    {
        // Wired member-routine marker: `$` is a separate Dollar token, recorded structurally in
        // _routineNameWired and dropped from the name (bare canonical name).
        if (CheckAndAdvance(type: TokenType.Dollar))
        {
            _routineNameWired = true;
        }

        // Accept keyword tokens that are also valid identifiers as memberRoutine names
        // (e.g. `BitArray[N].none()` — `none` is the absent-value keyword but reads
        // fine as a member-access name in postfix position).
        if (!Check(type: TokenType.Identifier) &&
            !IsKeywordValidAsMemberRoutineName(type: CurrentToken.Type))
        {
            throw ThrowParseError(code: GrammarDiagnosticCode.ExpectedIdentifier,
                message: errorMessage);
        }

        string name = CurrentToken.Text;
        Advance();

        // The failable `!` is NOT folded into the name — it stays a separate Bang token for the
        // declaration parser to consume into RoutineDeclaration.IsFailable (canonical bare name).
        return name;
    }

    /// <summary>
    /// Process an INDENT token by pushing a new indentation level.
    /// </summary>
    private void ProcessIndentToken()
    {
        if (!CheckAndAdvance(type: TokenType.Indent))
        {
            throw ThrowParseError(code: GrammarDiagnosticCode.ExpectedIndentedBlock,
                message: "Expected INDENT token");
        }

        _currentIndentationLevel++;
        _indentationStack.Push(item: _currentIndentationLevel);
    }

    /// <summary>
    /// Process a single DEDENT token by popping one indentation level.
    /// Each block should only process its own DEDENT, not consume all consecutive ones.
    /// </summary>
    private void ProcessDedentTokens()
    {
        // Check for unnecessary closing braces before processing dedents
        CheckUnnecessaryBrace();

        // Only process ONE DEDENT - each block is responsible for its own dedent
        if (Check(type: TokenType.Dedent) && !IsAtEnd)
        {
            Advance(); // Consume the DEDENT token

            if (_indentationStack.Count > 1) // Keep base level
            {
                _indentationStack.Pop();
                _currentIndentationLevel = _indentationStack.Peek();
            }
            else
            {
                throw ThrowParseError(code: GrammarDiagnosticCode.UnexpectedDedent,
                    message: "Unexpected dedent - no matching indent");
            }
        }
    }

    #endregion

    #region Argument Parsing

    /// <summary>
    /// Parses a single argument (named or positional).
    /// Named arguments have the form: name: expression
    /// </summary>
    private Expression ParseArgument()
    {
        SourceLocation location = GetLocation();

        // Single-hole `_` lambda: the boundary is THIS argument. Save + reset the flag so a `_` in an
        // outer argument doesn't leak in and a `_` here doesn't leak out; a nested call's ParseArgument
        // saves/resets again, giving the nearest-enclosing-argument boundary.
        bool savedSawHole = _sawHole;
        _sawHole = false;
        try
        {
            // Check for named argument: identifier followed by colon
            if (Check(type: TokenType.Identifier) && PeekToken(offset: 1)
                   .Type == TokenType.Colon)
            {
                string argName = CurrentToken.Text;
                Advance(); // consume identifier
                Advance(); // consume colon

                // Parse the value expression
                Expression value =
                    MaybeWrapHoleLambda(body: ParseExpression(), location: location);

                return new NamedArgumentExpression(Name: argName,
                    Value: value,
                    Location: location);
            }

            // Regular positional argument
            Expression expr = ParseExpression();

            // Check for dict entry literal: expr:expr (e.g., 1:2 in Dict(1:2, 3:4))
            // Named arguments (identifier: expr) are already handled above,
            // so this catches non-identifier keys like literals: 1:2, "key":val
            if (Check(type: TokenType.Colon))
            {
                Advance(); // consume colon
                Expression value = ParseExpression();
                return new DictEntryLiteralExpression(Key: expr, Value: value, Location: location);
            }

            return MaybeWrapHoleLambda(body: expr, location: location);
        }
        finally
        {
            _sawHole = savedSawHole;
        }
    }

    /// <summary>
    /// Wraps <paramref name="body"/> in a single-parameter lambda when a `_` placeholder was parsed
    /// within it (see <see cref="_sawHole"/>): `xs.map(_ * 2)` -> `xs.map(__rf_hole => __rf_hole * 2)`.
    /// All `_` occurrences in the argument share the ONE hole parameter. No-op when no hole was seen.
    /// </summary>
    private Expression MaybeWrapHoleLambda(Expression body, SourceLocation location)
    {
        if (!_sawHole)
        {
            return body;
        }

        var parameters = new List<Parameter>
        {
            new(Name: HoleParamName,
                Type: null,
                DefaultValue: null,
                Location: location)
        };
        return new LambdaExpression(Parameters: parameters,
            Body: body,
            Captures: null,
            Location: location);
    }

    /// <summary>
    /// Parses a comma-separated list of arguments (named or positional).
    /// Called after '(' has been consumed.
    /// </summary>
    /// <returns>List of argument expressions.</returns>
    private List<Expression> ParseArgumentList()
    {
        var args = new List<Expression>();

        // Argument lists re-enable bare lambdas inside a when-arm condition
        // (e.g. `items.any(pred: x => x > 0) => ...`).
        bool savedConditionContext = _inWhenConditionContext;
        _inWhenConditionContext = false;
        try
        {
            // Skip leading newlines
            while (Check(type: TokenType.Newline)) { Advance(); }

            if (!Check(type: TokenType.RightParen))
            {
                do
                {
                    // Skip newlines before each argument (for multi-line formatting)
                    while (Check(type: TokenType.Newline)) { Advance(); }

                    args.Add(item: ParseArgument());

                    // Skip newlines after each argument (before comma or closing paren)
                    while (Check(type: TokenType.Newline)) { Advance(); }
                } while (CheckAndAdvance(type: TokenType.Comma));
            }

            // Skip trailing newlines
            while (Check(type: TokenType.Newline)) { Advance(); }

            return args;
        }
        finally
        {
            _inWhenConditionContext = savedConditionContext;
        }
    }

    #endregion

    #region Collection Literals

    /// <summary>
    /// Parse list literal: [expr, expr, ...]
    /// The opening '[' has already been consumed.
    /// </summary>
    private ListLiteralExpression ParseListLiteral(SourceLocation location)
    {
        var elements = new List<Expression>();

        if (!Check(type: TokenType.RightBracket))
        {
            do
            {
                elements.Add(item: ParseExpression());
            } while (CheckAndAdvance(type: TokenType.Comma));
        }

        Consume(type: TokenType.RightBracket, errorMessage: "Expected ']' after list elements");

        return new ListLiteralExpression(Elements: elements,
            ElementType: null,
            Location: location);
    }

    /// <summary>
    /// Parse set or dict literal: {expr, expr, ...} or {key: value, ...}
    /// The opening '{' has already been consumed.
    /// Disambiguation: If the first element contains ':', it's a dict; otherwise it's a set.
    /// Empty {} is an empty set, {:} is an empty dict.
    /// </summary>
    private Expression ParseSetOrDictLiteral(SourceLocation location)
    {
        // {:} -> empty dict
        if (Check(type: TokenType.Colon))
        {
            Advance(); // consume ':'
            Consume(type: TokenType.RightBrace, errorMessage: "Expected '}' after '{:'");
            return new DictLiteralExpression(Pairs: [],
                KeyType: null,
                ValueType: null,
                Location: location);
        }

        // {} -> empty set
        if (CheckAndAdvance(type: TokenType.RightBrace))
        {
            return new SetLiteralExpression(Elements: [], ElementType: null, Location: location);
        }

        // Parse first element to determine if set or dict
        Expression firstExpr = ParseExpression();

        // If we see a colon, this is a dict literal
        if (CheckAndAdvance(type: TokenType.Colon))
        {
            return ParseDictLiteralContinuation(firstKey: firstExpr, location: location);
        }

        // Otherwise it's a set literal
        return ParseSetLiteralContinuation(firstElement: firstExpr, location: location);
    }

    /// <summary>
    /// Continue parsing dict literal after first key and colon: {key: value, ...}
    /// </summary>
    private DictLiteralExpression ParseDictLiteralContinuation(Expression firstKey,
        SourceLocation location)
    {
        var pairs = new List<(Expression Key, Expression Value)>();

        // Parse value for first key
        Expression firstValue = ParseExpression();
        pairs.Add(item: (firstKey, firstValue));

        // Parse remaining key-value pairs
        while (CheckAndAdvance(type: TokenType.Comma))
        {
            Expression key = ParseExpression();
            Consume(type: TokenType.Colon,
                errorMessage: "Expected ':' between dict key and value");
            Expression value = ParseExpression();
            pairs.Add(item: (key, value));
        }

        Consume(type: TokenType.RightBrace, errorMessage: "Expected '}' after dict elements");

        return new DictLiteralExpression(Pairs: pairs,
            KeyType: null,
            ValueType: null,
            Location: location);
    }

    /// <summary>
    /// Continue parsing set literal after first element: {expr, expr, ...}
    /// </summary>
    private SetLiteralExpression ParseSetLiteralContinuation(Expression firstElement,
        SourceLocation location)
    {
        var elements = new List<Expression> { firstElement };

        // Parse remaining elements
        while (CheckAndAdvance(type: TokenType.Comma))
        {
            elements.Add(item: ParseExpression());
        }

        Consume(type: TokenType.RightBrace, errorMessage: "Expected '}' after set elements");

        return new SetLiteralExpression(Elements: elements, ElementType: null, Location: location);
    }

    #endregion
}
