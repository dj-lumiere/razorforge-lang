using Compiler.Diagnostics;
using Compiler.Tokenizer;
using SyntaxTree;

namespace Compiler.Parser;

/// <summary>
/// Partial class containing expression parsing (precedence climbing chain).
/// </summary>
public partial class Parser
{
    /// <summary>
    /// Entry point for expression parsing. Delegates to the assignment level.
    /// </summary>
    /// <returns>The parsed <see cref="Expression"/> AST node.</returns>
    /// <remarks>Audited at 2025.12.21</remarks>
    private Expression ParseExpression()
    {
        return ParseAssignment();
    }

    /// <summary>
    /// Parses assignment expressions including compound assignments (lowest precedence).
    /// Syntax: <c>target = value</c>, <c>target += value</c>, etc.
    /// Right-associative to support chained assignments.
    /// Base compound assignments (+=, -=, etc.) emit CompoundAssignmentExpression for in-place dispatch.
    /// Overflow variants (+%=, +^=, etc.) and ??= expand to: <c>a +%= b</c> becomes <c>a = a +% b</c>.
    /// </summary>
    /// <returns>The parsed expression, possibly an assignment.</returns>
    /// <summary>
    /// Parses the inner expression of a comptime splice after the opening <c>${</c> has been
    /// consumed, up to and including the closing <c>}</c>.
    /// </summary>
    /// <param name="kind">The required fold kind, fixed by the syntactic position.</param>
    /// <returns>A <see cref="SpliceExpression"/> node.</returns>
    private SpliceExpression ParseSplice(SpliceKind kind)
    {
        SourceLocation loc = GetLocation(token: PeekToken(offset: -1));
        Expression inner = ParseExpression();
        Consume(type: TokenType.RightBrace, errorMessage: "Expected '}' to close '${...}' splice");
        return new SpliceExpression(Inner: inner, RequiredKind: kind, Location: loc);
    }

    /// <summary>
    /// Parses a brace-less comptime splice <c>$primary</c> after the leading <c>$</c> has been consumed.
    /// The <c>$</c> binds to a single self-delimiting primary — an identifier optionally followed by one
    /// call (<c>$nameof(m)</c>, <c>$typeof(m)</c>, <c>$valueof(c)</c>) — and stops at a following <c>.</c>
    /// (chaining continues on the splice RESULT: <c>me.$nameof(m).foo</c> → <c>me.x.foo</c>).
    /// </summary>
    /// <param name="kind">The required fold kind, fixed by the syntactic position.</param>
    private SpliceExpression ParseDollarSplice(SpliceKind kind)
    {
        SourceLocation loc = GetLocation(token: PeekToken(offset: -1));
        Expression inner = ParseDollarSpliceInner();
        return new SpliceExpression(Inner: inner, RequiredKind: kind, Location: loc);
    }

    /// <summary>The self-delimiting primary a <c>$</c> splice binds to: an identifier, optionally with a
    /// single call argument list. Deliberately does NOT consume a trailing <c>.member</c> — that chains
    /// on the splice result.</summary>
    private Expression ParseDollarSpliceInner()
    {
        SourceLocation loc = GetLocation();
        if (!Check(type: TokenType.Identifier) && !IsKeywordValidAsMemberRoutineName(CurrentToken.Type))
        {
            throw ThrowParseError(code: GrammarDiagnosticCode.ExpectedIdentifier,
                message: "Expected a primary (e.g. 'nameof(m)') after a '$' comptime splice.");
        }
        string name = CurrentToken.Text;
        Advance();
        Expression expr = new IdentifierExpression(Name: name, Location: loc);
        if (CheckAndAdvance(type: TokenType.LeftParen))
        {
            List<Expression> args = ParseArgumentList();
            Consume(type: TokenType.RightParen, errorMessage: ExpectedRightParenAfterArguments);
            expr = new CallExpression(Callee: expr, Arguments: args, Location: loc);
        }
        return expr;
    }

    private Expression ParsePrimary()
    {
        SourceLocation location = GetLocation();

        // Comptime splice in expression position: ${expr} (legacy) or $primary (brace-less).
        if (CheckAndAdvance(type: TokenType.SpliceOpen))
        {
            return ParseSplice(kind: SpliceKind.Value);
        }

        if (CheckAndAdvance(type: TokenType.Dollar))
        {
            return ParseDollarSplice(kind: SpliceKind.Value);
        }

        // Boolean and none literals
        if (CheckAndAdvance(type: TokenType.True))
        {
            return new LiteralExpression(Value: true,
                LiteralType: TokenType.True,
                Location: location);
        }

        if (CheckAndAdvance(type: TokenType.False))
        {
            return new LiteralExpression(Value: false,
                LiteralType: TokenType.False,
                Location: location);
        }

        // `none` (lowercase) — the absent value literal. Carrier-slot-only (gated downstream).
        if (CheckAndAdvance(type: TokenType.NoneValue))
        {
            return new LiteralExpression(Value: null!,
                LiteralType: TokenType.NoneValue,
                Location: location);
        }

        // Numeric, text, character, byte-size, and duration literals
        if (TryParseLiteralPrimary(location: location, result: out Expression? literalExpr))
        {
            return literalExpr!;
        }

        // Arrow lambda expression: x => expr or x given y => expr (single parameter, no parens)
        if (IsArrowLambdaStart())
        {
            return ParseArrowLambdaExpression(location: location);
        }

        // Identifiers and language-specific keywords
        // Note: 'me' is tokenized as TokenType.Me and 'Me' (the self TYPE) as TokenType.MyType, so we
        // handle both explicitly. `Me` in expression position is a self-type reference usable as a
        // constructor callee — e.g. the choice/flags `all_cases()` derive reconstructs each case via the
        // reverse `Me(from: $valueof(c))` constructor; after the derive-template T→concrete substitution
        // `Me` resolves to the owning type exactly as it does in a hand-written routine body.
        if (CheckAndAdvance(TokenType.Identifier, TokenType.Me, TokenType.MyType))
        {
            return ParseIdentifierPrimary(location: location);
        }

        // Parenthesized expression, tuple literal, or arrow lambda with parenthesized params
        if (CheckAndAdvance(type: TokenType.LeftParen))
        {
            return ParseParenthesizedPrimary(location: location);
        }

        // When expression: when x { pattern => expr, ... }
        // Used in expression context: return when x { ... }, var y = when x { ... }
        if (CheckAndAdvance(type: TokenType.When))
        {
            return ParseWhenExpression(location: location);
        }

        // List literal: [expr, expr, ...]
        if (CheckAndAdvance(type: TokenType.LeftBracket))
        {
            return ParseListLiteral(location: location);
        }

        // Set or Dict literal: {expr, expr, ...} or {key: value, ...}
        if (CheckAndAdvance(type: TokenType.LeftBrace))
        {
            return ParseSetOrDictLiteral(location: location);
        }

        throw ThrowParseError(code: GrammarDiagnosticCode.ExpectedExpression,
            message: $"Unexpected token: {CurrentToken.Type}");
    }

    /// <summary>
    /// Parses a primary starting with an identifier / <c>me</c> token that has already been consumed:
    /// the <c>me</c> receiver, a single-hole <c>_</c> lambda placeholder, a realm-qualified reference
    /// (<c>RF::Core.List</c>), or a plain identifier.
    /// </summary>
    private IdentifierExpression ParseIdentifierPrimary(SourceLocation location)
    {
        string text = PeekToken(offset: -1)
           .Text;
        if (text == "me")
        {
            return new IdentifierExpression(Name: "me", Location: location);
        }

        // Single-hole `_` lambda: `_` in expression position is the placeholder for the sole
        // parameter of an implicit lambda. Parse it as a reference to the reserved hole name and
        // flag it; ParseArgument wraps the enclosing argument into `LambdaExpression([<hole>], …)`.
        // A stray `_` that no ParseArgument wraps stays an unknown-identifier reference (an error),
        // exactly as before. Pattern/discard `_` is handled by their own parse paths, not here.
        if (text == "_")
        {
            _sawHole = true;
            return new IdentifierExpression(Name: HoleParamName, Location: location);
        }

        // Realm-qualified reference in expression position: `RF::Core.List` (e.g. a
        // `RF::Core.List[S64]()` constructor call inside a Suflae wrapper). The leading ident is the
        // realm tag; consume the `.`/`/`-segmented qualified name and carry the realm so SA resolves
        // it in the RazorForge/bare realm. Postfix `[..]` / `(..)` then apply as usual.
        if (Check(type: TokenType.DoubleColon))
        {
            Advance();
            var realmSb = new System.Text.StringBuilder(
                ConsumeIdentifier(errorMessage: "Expected name after realm qualifier '::'"));
            while (Check(type: TokenType.Dot) || Check(type: TokenType.Slash))
            {
                char segSep;
                if (CheckAndAdvance(type: TokenType.Dot))
                {
                    segSep = '.';
                }
                else
                {
                    CheckAndAdvance(type: TokenType.Slash);
                    segSep = '/';
                }
                realmSb.Append(segSep);
                realmSb.Append(ConsumeIdentifier(
                    errorMessage: "Expected name component after '.'/'/' in realm-qualified reference"));
            }
            return new IdentifierExpression(Name: realmSb.ToString(), Location: location, Realm: text);
        }

        return new IdentifierExpression(Name: text, Location: location);
    }

    /// <summary>
    /// Parses a primary starting with <c>(</c> that has already been consumed: an arrow lambda with
    /// parenthesized parameters, a tuple literal, or a plain parenthesized expression. Suspends the
    /// when-arm condition context so bare lambdas inside the parentheses still parse.
    /// </summary>
    private Expression ParseParenthesizedPrimary(SourceLocation location)
    {
        // Parentheses re-enable bare lambdas inside a when-arm condition.
        bool savedConditionContext = _inWhenConditionContext;
        _inWhenConditionContext = false;
        try
        {
            if (IsArrowLambdaParameters())
            {
                return ParseParenthesizedArrowLambda(location: location);
            }

            // Parse first expression
            Expression firstExpr = ParseExpression();

            // Check if this is a tuple (has comma) or just parenthesized expression
            if (CheckAndAdvance(type: TokenType.Comma))
            {
                return ParseTupleLiteralTail(firstExpr: firstExpr, location: location);
            }

            // Just a parenthesized expression
            Consume(type: TokenType.RightParen, errorMessage: "Expected ')' after expression");
            return firstExpr;
        }
        finally
        {
            _inWhenConditionContext = savedConditionContext;
        }
    }

    /// <summary>
    /// Parses the remainder of a tuple literal after the first element and its trailing comma have been
    /// consumed: a single-element tuple (<c>(expr,)</c>) or a multi-element tuple.
    /// </summary>
    private TupleLiteralExpression ParseTupleLiteralTail(Expression firstExpr, SourceLocation location)
    {
        var elements = new List<Expression> { firstExpr };

        // Check for single-element tuple: (expr,)
        if (Check(type: TokenType.RightParen))
        {
            Consume(type: TokenType.RightParen, errorMessage: "Expected ')' after tuple");
            return new TupleLiteralExpression(Elements: elements, Location: location);
        }

        // Multi-element tuple: (expr1, expr2, ...)
        do
        {
            elements.Add(item: ParseExpression());
        } while (CheckAndAdvance(type: TokenType.Comma) && !Check(type: TokenType.RightParen));

        Consume(type: TokenType.RightParen,
            errorMessage: "Expected ')' after tuple elements");
        return new TupleLiteralExpression(Elements: elements, Location: location);
    }

    private WhenExpression ParseWhenExpression(SourceLocation location)
    {
        Expression? subject = ParseWhenExpressionSubject(isConditionBased: out bool isConditionBased);

        Consume(type: TokenType.Newline, errorMessage: "Expected newline after when expression");

        if (!Check(type: TokenType.Indent))
        {
            throw ThrowParseError(code: GrammarDiagnosticCode.ExpectedIndentedBlock,
                message: "Expected indented block after when");
        }

        ProcessIndentToken();

        var clauses = new List<WhenClause>();

        while (!Check(type: TokenType.Dedent) && !IsAtEnd)
        {
            if (CheckAndAdvance(TokenType.Newline, TokenType.DocComment))
            {
                continue;
            }

            SourceLocation clauseLocation = GetLocation();
            Pattern pattern = ParseWhenExpressionPattern(isConditionBased: isConditionBased,
                clauseLocation: clauseLocation);
            Statement body = ParseWhenExpressionArmBody();

            clauses.Add(item: new WhenClause(Pattern: pattern, Body: body, Location: GetLocation()));
            CheckAndAdvance(TokenType.Comma, TokenType.Newline);
        }

        if (Check(type: TokenType.Dedent))
        {
            ProcessDedentTokens();
        }
        else if (!IsAtEnd)
        {
            throw ThrowParseError(code: GrammarDiagnosticCode.ExpectedDedent,
                message: "Expected dedent after when clauses");
        }

        return new WhenExpression(Expression: subject, Clauses: clauses, Location: location);
    }

    /// <summary>
    /// Parses the subject of a when-expression: a bare/`true` newline form is condition-based (no
    /// subject); anything else is the subject expression. Reports which form via
    /// <paramref name="isConditionBased"/>.
    /// </summary>
    private Expression? ParseWhenExpressionSubject(out bool isConditionBased)
    {
        if (Check(type: TokenType.Newline))
        {
            isConditionBased = true;
            return null;
        }

        if (Check(type: TokenType.True) && PeekToken(offset: 1).Type == TokenType.Newline)
        {
            isConditionBased = true;
            Advance();
            return null;
        }

        isConditionBased = false;
        return ParseExpression();
    }

    /// <summary>
    /// Parses a single when-expression clause pattern: <c>else</c>/<c>else name</c>, a condition-based
    /// expression pattern, an <c>is</c>/<c>isnot</c> type or flags pattern, a comparison pattern, or a
    /// general pattern.
    /// </summary>
    private Pattern ParseWhenExpressionPattern(bool isConditionBased, SourceLocation clauseLocation)
    {
        if (CheckAndAdvance(type: TokenType.Else))
        {
            return ParseElseClausePattern(clauseLocation: clauseLocation);
        }

        if (isConditionBased)
        {
            bool savedConditionContext = _inWhenConditionContext;
            _inWhenConditionContext = true;
            Expression condExpr;
            try
            {
                condExpr = ParseExpression();
            }
            finally
            {
                _inWhenConditionContext = savedConditionContext;
            }

            return new ExpressionPattern(Expression: condExpr, Location: clauseLocation);
        }

        if (CheckAndAdvance(type: TokenType.Is))
        {
            return ParseIsWhenPattern();
        }

        if (CheckAndAdvance(type: TokenType.IsNot))
        {
            return ParseIsNotWhenPattern(clauseLocation: clauseLocation);
        }

        if (IsComparisonOperator(tokenType: CurrentToken.Type))
        {
            return ParseComparisonPattern();
        }

        _inWhenPatternContext = true;
        Pattern generalPattern = ParsePattern();
        _inWhenPatternContext = false;
        return generalPattern;
    }

    /// <summary>
    /// Parses an <c>else</c> clause pattern after the <c>else</c> keyword has been consumed, with an
    /// optional variable binding (<c>else name</c>).
    /// </summary>
    private ElsePattern ParseElseClausePattern(SourceLocation clauseLocation)
    {
        if (Check(type: TokenType.Identifier))
        {
            TokenType nextAfterIdent = PeekToken(offset: 1).Type;
            if (nextAfterIdent is TokenType.FatArrow or TokenType.Newline)
            {
                string varName = ConsumeIdentifier(errorMessage: "Expected variable name after 'else'");
                return new ElsePattern(VariableName: varName, Location: clauseLocation);
            }

            return new ElsePattern(VariableName: null, Location: clauseLocation);
        }

        return new ElsePattern(VariableName: null, Location: clauseLocation);
    }

    /// <summary>
    /// Parses a when-expression arm body after its pattern: <c>=&gt;</c> followed by either an indented
    /// block or a single-expression statement.
    /// </summary>
    private Statement ParseWhenExpressionArmBody()
    {
        Statement body;
        _inWhenClauseBody = true;

        if (CheckAndAdvance(type: TokenType.FatArrow))
        {
            if (Check(type: TokenType.Newline) && PeekToken(offset: 1).Type == TokenType.Indent)
            {
                Advance();
                body = ParseIndentedBlock();
            }
            else
            {
                Expression armExpr = ParseExpression();
                body = new ExpressionStatement(Expression: armExpr, Location: armExpr.Location);
            }
        }
        else
        {
            Consume(type: TokenType.FatArrow, errorMessage: "Expected '=>' after when pattern");
            Expression armExpr = ParseExpression();
            body = new ExpressionStatement(Expression: armExpr, Location: armExpr.Location);
        }

        _inWhenClauseBody = false;
        return body;
    }

    /// <summary>
    /// Tries to parse any of the typed literal primary forms at the current token position: numeric
    /// (integer and float), inserted text (f-strings), text, character, byte-size, and duration
    /// literals. Returns <c>true</c> and sets <paramref name="result"/> when one is consumed; returns
    /// <c>false</c> when the current token does not start a literal.
    /// </summary>
    private bool TryParseLiteralPrimary(SourceLocation location, out Expression? result)
    {
        if (TryParseNumericLiteral(location: location, result: out result)) return true;
        if (TryParseInsertedText(location: location, result: out result)) return true;
        if (TryParseTextLiteral(location: location, result: out result)) return true;
        if (TryParseCharacterLiteral(location: location, result: out result)) return true;
        if (TryParseByteSizeLiteral(location: location, result: out result)) return true;
        if (TryParseDurationLiteral(location: location, result: out result)) return true;
        result = null;
        return false;
    }

    /// <summary>
    /// Parses the pattern body of an <c>is</c> when-clause after the <c>is</c> keyword has been consumed:
    /// either a flags-combined pattern (<c>is A and B</c>) or a simple type pattern (<c>is TypeName</c>).
    /// </summary>
    private Pattern ParseIsWhenPattern()
    {
        _inWhenPatternContext = true;
        Pattern pattern;
        if (Check(type: TokenType.Identifier) &&
            PeekToken(offset: 1).Type is TokenType.And or TokenType.Or or TokenType.But)
        {
            pattern = ParseFlagsIsWhenPattern();
        }
        else if (Check(type: TokenType.None) || Check(type: TokenType.Identifier))
        {
            pattern = ParseTypePattern();
        }
        else
        {
            throw ThrowParseError(code: GrammarDiagnosticCode.InvalidPattern,
                message: $"'is' must be followed by a type name. For value comparisons, use '== {CurrentToken.Text}' instead of 'is {CurrentToken.Text}'.");
        }

        _inWhenPatternContext = false;
        return pattern;
    }

    /// <summary>
    /// Parses the pattern body of an <c>isnot</c> when-clause after the <c>isnot</c> keyword has been consumed:
    /// a negated type pattern (<c>isnot TypeName</c>).
    /// </summary>
    private Pattern ParseIsNotWhenPattern(SourceLocation clauseLocation)
    {
        _inWhenPatternContext = true;
        Pattern pattern;
        if (Check(type: TokenType.None) || Check(type: TokenType.Identifier))
        {
            TypeExpression type = ParseType();
            pattern = new NegatedTypePattern(Type: type, Location: clauseLocation);
        }
        else
        {
            throw ThrowParseError(code: GrammarDiagnosticCode.InvalidPattern,
                message: "'isnot' must be followed by a type name.");
        }

        _inWhenPatternContext = false;
        return pattern;
    }

    /// <summary>
    /// Returns true when the current token position looks like the start of a bare arrow lambda
    /// (<c>x =&gt; expr</c> or <c>x given y =&gt; expr</c>): an identifier followed by
    /// <c>=&gt;</c> or <c>given</c>, outside any when-pattern or when-condition context.
    /// </summary>
    private bool IsArrowLambdaStart() =>
        !_inWhenPatternContext && !_inWhenConditionContext &&
        Check(type: TokenType.Identifier) &&
        (PeekToken(offset: 1).Type == TokenType.FatArrow ||
         PeekToken(offset: 1).Type == TokenType.Given);

}
