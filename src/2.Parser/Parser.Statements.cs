using Compiler.Diagnostics;
using Compiler.Tokenizer;
using SyntaxTree;

namespace Compiler.Parser;

/// <summary>
/// Partial class containing statement parsing (if, while, for, when, return, using, release, danger, etc.).
/// Handles both RazorForge and Suflae syntax via <c>_language</c> dispatch.
/// </summary>
public partial class Parser
{
    // ═══════════════════════════════════════════════════════════════════════════════
    // CONTROL FLOW
    // ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Parses an if statement with optional elseif/else chains.
    /// Syntax: <c>if condition</c> followed by indented body
    /// </summary>
    /// <remarks>
    /// Parsing phases:
    ///
    /// PHASE 1: INITIAL IF
    ///   - Parse condition expression
    ///   - Parse body (indented block or brace block)
    ///
    /// PHASE 2: ELSEIF CHAIN (optional)
    ///   - Parse each elseif as a nested IfStatement
    ///   - Chain them together via ElseStatement
    ///
    /// PHASE 3: FINAL ELSE (optional)
    ///   - Parse else block
    ///   - Attach to end of elseif chain
    ///
    /// The elseif chain is desugared to nested if-else:
    ///   if a:       becomes    IfStatement(a, then1,
    ///       then1                  IfStatement(b, then2,
    ///   elseif b:                      else3))
    ///       then2
    ///   else:
    ///       else3
    /// </remarks>
    /// <returns>An <see cref="IfStatement"/> AST node.</returns>
    private IfStatement ParseIfStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        // ═══════════════════════════════════════════════════════════════════════════
        // PHASE 1: INITIAL IF (condition and then-block)
        // ═══════════════════════════════════════════════════════════════════════════
        Expression condition = ParseExpression();
        Statement thenBranch = ParseBody();

        Statement? elseBranch = null;

        // ═══════════════════════════════════════════════════════════════════════════
        // PHASE 2: ELSEIF CHAIN (convert to nested if-else)
        // ═══════════════════════════════════════════════════════════════════════════
        while (Match(type: TokenType.Elseif))
        {
            SourceLocation elseifLocation = GetLocation(token: PeekToken(offset: -1));
            Expression elseifCondition = ParseExpression();
            Statement elseifBranch = ParseBody();

            // Create nested if statement for this elseif
            var nestedIf = new IfStatement(Condition: elseifCondition,
                ThenStatement: elseifBranch,
                ElseStatement: null,
                Location: elseifLocation);

            // Attach to the chain
            if (elseBranch == null)
            {
                elseBranch = nestedIf;
            }
            else if (elseBranch is IfStatement prevIf)
            {
                // Find the end of the elseif chain and attach
                elseBranch = AttachElseBranch(root: prevIf, newBranch: nestedIf);
            }
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // PHASE 3: FINAL ELSE (optional)
        // ═══════════════════════════════════════════════════════════════════════════
        if (!Match(type: TokenType.Else))
        {
            return new IfStatement(Condition: condition,
                ThenStatement: thenBranch,
                ElseStatement: elseBranch,
                Location: location);
        }

        Statement finalElse = ParseBody();

        if (elseBranch == null)
        {
            elseBranch = finalElse;
        }
        else if (elseBranch is IfStatement lastIf)
        {
            // Attach final else to the end of the elseif chain
            elseBranch = AttachElseBranch(root: lastIf, newBranch: finalElse);
        }

        return new IfStatement(Condition: condition,
            ThenStatement: thenBranch,
            ElseStatement: elseBranch,
            Location: location);
    }

    /// <summary>
    /// Helper to recursively attach an else branch to the end of an if-elseif chain.
    /// Since IfStatement is immutable, we need to rebuild the chain.
    /// </summary>
    private static IfStatement AttachElseBranch(IfStatement root, Statement newBranch)
    {
        if (root.ElseStatement == null)
        {
            return new IfStatement(Condition: root.Condition,
                ThenStatement: root.ThenStatement,
                ElseStatement: newBranch,
                Location: root.Location);
        }

        if (root.ElseStatement is IfStatement nestedIf)
        {
            return new IfStatement(Condition: root.Condition,
                ThenStatement: root.ThenStatement,
                ElseStatement: AttachElseBranch(root: nestedIf, newBranch: newBranch),
                Location: root.Location);
        }

        // Already has a non-if else branch, shouldn't happen
        return root;
    }

    /// <summary>
    /// Parses an unless statement (inverted if).
    /// Syntax: <c>unless condition</c> = <c>if not condition</c>, followed by indented body.
    /// </summary>
    /// <returns>An <see cref="IfStatement"/> with negated condition.</returns>
    private IfStatement ParseUnlessStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        Expression condition = ParseExpression();
        Statement thenBranch = ParseBody();
        Statement? elseBranch = null;

        if (Match(type: TokenType.Else))
        {
            elseBranch = ParseBody();
        }

        // Unless is "if not condition"
        var negatedCondition = new UnaryExpression(Operator: UnaryOperator.Not,
            Operand: condition,
            Location: condition.Location);

        return new IfStatement(Condition: negatedCondition,
            ThenStatement: thenBranch,
            ElseStatement: elseBranch,
            Location: location);
    }

    /// <summary>
    /// Parses a while loop statement.
    /// Syntax: <c>while condition</c> followed by indented body
    /// Optional <c>else</c> block executes if the loop completes without hitting a break.
    /// </summary>
    /// <returns>A <see cref="WhileStatement"/> AST node.</returns>
    private WhileStatement ParseWhileStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        Expression condition = ParseExpression();
        Statement body = ParseBody();

        // Check for else clause (runs if loop completes without break)
        Statement? elseBranch = null;
        if (Match(type: TokenType.Else))
        {
            elseBranch = ParseBody();
        }

        return new WhileStatement(Condition: condition,
            Body: body,
            ElseBranch: elseBranch,
            Location: location);
    }

    /// <summary>
    /// Parses a loop statement (infinite loop).
    /// Syntax: <c>loop</c> followed by indented body
    /// Equivalent to <c>while true</c>.
    /// </summary>
    /// <returns>A <see cref="WhileStatement"/> AST node with true condition.</returns>
    private LoopStatement ParseLoopStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        Statement body = ParseBody();
        return new LoopStatement(Body: body, Location: location);
    }

    /// <summary>
    /// Parses a for-in loop statement.
    /// Syntax: <c>for variable in iterable</c> or <c>for (a, b) in iterable</c> followed by body.
    /// Optional <c>else</c> block executes if loop completes without break.
    /// </summary>
    /// <returns>A <see cref="EachStatement"/> AST node.</returns>
    private EachStatement ParseEachStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        string? variable = null;
        DestructuringPattern? variablePattern = null;

        // Check for tuple destructuring: for (index, item) in items.enumerate()
        if (Check(type: TokenType.LeftParen))
        {
            List<DestructuringBinding> bindings = ParseDestructuringBindings();
            variablePattern = new DestructuringPattern(Bindings: bindings, Location: location);
        }
        else
        {
            variable = ConsumeIdentifier(errorMessage: "Expected variable name");
        }

        Consume(type: TokenType.In, errorMessage: "Expected 'in' in for loop");
        Expression sequenceable = ParseExpression();
        Statement body = ParseBody();

        // Check for else clause (runs if loop completes without break)
        Statement? elseBranch = null;
        if (Match(type: TokenType.Else))
        {
            elseBranch = ParseBody();
        }

        return new EachStatement(Variable: variable,
            VariablePattern: variablePattern,
            Iterable: sequenceable,
            Body: body,
            ElseBranch: elseBranch,
            Location: location);
    }

    /// <summary>
    /// Parses a compile-time member-expansion loop.
    /// Syntax (Phase 1): <c>expand m in allmemvarof(T)</c> followed by an indented body.
    /// Unlike <c>each</c> there is no <c>else</c> clause — the loop is unrolled at monomorphization.
    /// </summary>
    /// <returns>An <see cref="ExpandStatement"/> AST node.</returns>
    private ExpandStatement ParseExpandStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        string handle = ConsumeIdentifier(errorMessage: "Expected expand handle name");
        Consume(type: TokenType.In, errorMessage: "Expected 'in' in expand loop");

        // The source is an ordinary BuilderExpansion intrinsic call `<name>(T)` — the parser does NOT
        // know the intrinsic names (they are module routines, siblings of `nameof`/`typeof`, no longer
        // keywords). It reads the source NAME + TYPE syntactically; SemanticVerifier classifies the name
        // into an ExpandSourceKind (`allmemvarof`/`openmemvarof`/`caseof`) and gates it on
        // `import BuilderExpansion`.
        string sourceName = ConsumeIdentifier(errorMessage:
            "Expected an expand source (e.g. 'allmemvarof', 'openmemvarof', 'caseof') after 'in'");
        Consume(type: TokenType.LeftParen, errorMessage: $"Expected '(' after '{sourceName}'");
        TypeExpression sourceType = ParseType();
        Consume(type: TokenType.RightParen, errorMessage: $"Expected ')' after {sourceName}(...) type");

        Statement body = ParseBody();

        return new ExpandStatement(HandleName: handle,
            SourceName: sourceName,
            SourceType: sourceType,
            Body: body,
            Location: location);
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // WHEN (PATTERN MATCHING)
    // ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Parses a when (pattern matching) statement.
    /// Mandatory <c>=&gt;</c> on all arms.
    /// Both languages use indentation-based arms.
    /// </summary>
    /// <remarks>
    /// Two forms of when statements:
    /// 1. Subject-based: when expr { is Type =&gt; ..., LITERAL =&gt; ..., else =&gt; ... }
    /// 2. Condition-based (RF only): when { condition1 =&gt; ..., condition2 =&gt; ..., else =&gt; ... }
    ///
    /// Pattern types supported:
    /// - 'else' / 'else varName' - default case (wildcard or binding)
    /// - '_' - explicit wildcard
    /// - 'is Type' / 'is Type varName' - type pattern with optional binding
    /// - 'is Type (field1, field2)' - destructuring pattern
    /// - 'isnot Type' - negated type pattern
    /// - comparison operators (==, !=, &lt;, &gt;, &lt;=, &gt;=)
    /// - literal values (42, "hello", true)
    /// - expression patterns (for condition-based when)
    /// </remarks>
    /// <returns>A <see cref="WhenStatement"/> AST node.</returns>
    private WhenStatement ParseWhenStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        // ═══════════════════════════════════════════════════════════════════════════
        // PHASE 1: Determine when form (subject-based vs condition-based)
        // ═══════════════════════════════════════════════════════════════════════════
        // Subject-based:    when value { is Type => ... }     (RF)
        //                   when value\n    is Type => ...     (SF)
        // Condition-based:  when { x > 0 => ... }             (RF only)
        //                   when true { x > 0 => ... }        (RF only)
        // ═══════════════════════════════════════════════════════════════════════════

        Expression expression = ParseWhenStatementSubject(location: location,
            isConditionBased: out bool isConditionBased);

        // Indentation-delimited when block for both languages
        Consume(type: TokenType.Newline, errorMessage: "Expected newline after when expression");

        if (!Check(type: TokenType.Indent))
        {
            throw ThrowParseError(code: GrammarDiagnosticCode.ExpectedIndentedBlock,
                message: "Expected indented block after when");
        }

        ProcessIndentToken();

        // ═══════════════════════════════════════════════════════════════════════════
        // PHASE 2: Parse clauses (pattern => body)
        // ═══════════════════════════════════════════════════════════════════════════

        var clauses = new List<WhenClause>();
        WhenArmExpansion? armExpansion = null;

        bool AtClauseEnd()
        {
            return Check(type: TokenType.Dedent) || IsAtEnd;
        }

        while (!AtClauseEnd())
        {
            // Skip newlines and doc comments between clauses
            if (Match(TokenType.Newline, TokenType.DocComment))
            {
                continue;
            }

            // Comptime arm-expansion: `expand m in branchof(T)` generates one type-dispatch clause per
            // variant arm at monomorphization. It may sit ALONGSIDE explicit clauses (e.g. an
            // `is None => …` clause the payload-arm expansion doesn't cover) — at most one per `when`.
            if (Check(type: TokenType.Expand))
            {
                if (armExpansion != null)
                {
                    throw ThrowParseError(code: GrammarDiagnosticCode.InvalidPattern,
                        message: "A 'when' may contain at most one 'expand' arm-expansion.");
                }

                armExpansion = ParseWhenArmExpansion();
                continue;
            }

            SourceLocation clauseLocation = GetLocation();
            Pattern pattern = ParseWhenStatementPattern(isConditionBased: isConditionBased,
                clauseLocation: clauseLocation);

            // ─────────────────────────────────────────────────────────────────────
            // Arm body: either `=> expression` (single-line) or indented block
            // ─────────────────────────────────────────────────────────────────────

            Statement body = ParseWhenClauseBody();

            clauses.Add(
                item: new WhenClause(Pattern: pattern, Body: body, Location: GetLocation()));

            // Optional comma or newline between clauses
            Match(TokenType.Comma, TokenType.Newline);
        }

        // Close the when block (indentation-based)
        if (Check(type: TokenType.Dedent))
        {
            ProcessDedentTokens();
        }
        else if (!IsAtEnd)
        {
            throw ThrowParseError(code: GrammarDiagnosticCode.ExpectedDedent,
                message: "Expected dedent after when clauses");
        }

        return new WhenStatement(Expression: expression, Clauses: clauses, Location: location,
            ArmExpansion: armExpansion);
    }

    /// <summary>
    /// Parses the subject of a when-STATEMENT, distinguishing the condition-based forms
    /// (<c>when</c>-newline and <c>when true</c>-newline, both yielding a <c>true</c> literal subject)
    /// from a subject-based expression. Reports which form via <paramref name="isConditionBased"/>.
    /// </summary>
    private Expression ParseWhenStatementSubject(SourceLocation location, out bool isConditionBased)
    {
        // Check for condition-based forms:
        // 1. `when true\n` - explicit condition-based
        // 2. `when\n` - bare when (no subject) = condition-based
        if (Check(type: TokenType.Newline))
        {
            // Bare `when` followed by newline — condition-based with no subject
            isConditionBased = true;
            return new LiteralExpression(Value: true,
                LiteralType: TokenType.True,
                Location: location);
        }

        if (Check(type: TokenType.True))
        {
            // Peek ahead to see if this is `when true\n  INDENT` (condition-based)
            // vs `when true_var\n  INDENT` (subject-based with a variable named starting with true)
            Token nextToken = PeekToken(offset: 1);
            if (nextToken.Type == TokenType.Newline)
            {
                isConditionBased = true;
                Advance(); // consume 'true'
                return new LiteralExpression(Value: true,
                    LiteralType: TokenType.True,
                    Location: location);
            }
        }

        isConditionBased = false;
        return ParseExpression();
    }

    /// <summary>
    /// Parses a single when-STATEMENT clause pattern, dispatching (in order) to: <c>else</c>/
    /// <c>else name</c>, a condition-based expression pattern, an <c>is</c> flags/type pattern, an
    /// <c>isnot</c> negated type pattern, a comparison pattern, or a general pattern.
    /// </summary>
    private Pattern ParseWhenStatementPattern(bool isConditionBased, SourceLocation clauseLocation)
    {
        // ─────────────────────────────────────────────────────────────────────
        // Pattern dispatch: determine which pattern type we're parsing
        // Order matters - check specific patterns before general ones
        // ─────────────────────────────────────────────────────────────────────

        // Case 1: 'else' keyword - default/fallback case
        if (Match(type: TokenType.Else))
        {
            return ParseElseStatementPattern(clauseLocation: clauseLocation);
        }

        // Case 2: Condition-based when (RF only) - parse full expression as pattern
        if (isConditionBased)
        {
            return ParseConditionBasedPattern(clauseLocation: clauseLocation);
        }

        // Case 3: 'is' keyword - type pattern
        if (Match(type: TokenType.Is))
        {
            _inWhenPatternContext = true;
            Pattern pattern;
            // Check if this is a flags pattern: identifier followed by and/or/but
            if (Check(type: TokenType.Identifier) && PeekToken(offset: 1)
                   .Type is TokenType.And or TokenType.Or or TokenType.But)
            {
                pattern = ParseFlagsIsWhenPattern();
            }
            // 'is' must be followed by a type/variant name
            else if (Check(type: TokenType.None) || Check(type: TokenType.Identifier))
            {
                pattern = ParseTypePattern();
            }
            else
            {
                throw ThrowParseError(code: GrammarDiagnosticCode.InvalidPattern,
                    message:
                    $"'is' must be followed by a type name. For value comparisons, use '== {CurrentToken.Text}' instead of 'is {CurrentToken.Text}'.");
            }

            _inWhenPatternContext = false;
            return pattern;
        }

        // Case 4: 'isnot' keyword - negated type pattern (no variable binding)
        if (Match(type: TokenType.IsNot))
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

        // Case 6: Comparison patterns (==, !=, <, >, <=, >=)
        if (IsComparisonOperator(tokenType: CurrentToken.Type))
        {
            return ParseComparisonPattern();
        }

        // Case 7: Other patterns (wildcards, literals, identifiers)
        // Set context flag to prevent single-param lambdas from being parsed
        // inside when patterns (e.g., a < b => action should not treat b => action as lambda)
        _inWhenPatternContext = true;
        Pattern generalPattern = ParsePattern();
        _inWhenPatternContext = false;
        return generalPattern;
    }

    /// <summary>
    /// Parses a full expression as an <see cref="ExpressionPattern"/> inside a condition-based
    /// <c>when</c> block (RF only), with the <c>_inWhenConditionContext</c> flag set for the duration
    /// so the expression parser handles <c>=&gt;</c> correctly.
    /// </summary>
    private ExpressionPattern ParseConditionBasedPattern(SourceLocation clauseLocation)
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

    /// <summary>
    /// Parses a when-STATEMENT <c>else</c> clause pattern after the <c>else</c> keyword has been
    /// consumed, with an optional variable binding (<c>else name</c>).
    /// </summary>
    private ElsePattern ParseElseStatementPattern(SourceLocation clauseLocation)
    {
        // Check for variable binding: else varName => ... or else varName\n INDENT
        if (Check(type: TokenType.Identifier))
        {
            TokenType nextAfterIdent = PeekToken(offset: 1)
               .Type;
            if (nextAfterIdent is TokenType.FatArrow or TokenType.Newline)
            {
                string varName =
                    ConsumeIdentifier(errorMessage: "Expected variable name after 'else'");
                return new ElsePattern(VariableName: varName, Location: clauseLocation);
            }

            return new ElsePattern(VariableName: null, Location: clauseLocation);
        }

        // Plain else without variable binding
        return new ElsePattern(VariableName: null, Location: clauseLocation);
    }

    /// <summary>
    /// Parses a when-clause body: <c>=> statement</c>, <c>=> pass</c>, or <c>=>\n INDENT block</c>.
    /// </summary>
    private Statement ParseWhenClauseBody()
    {
        Statement body;
        _inWhenClauseBody = true;
        if (Match(type: TokenType.FatArrow))
        {
            // After =>, check for block form: => \n INDENT block DEDENT
            if (Check(type: TokenType.Newline) && PeekToken(offset: 1).Type == TokenType.Indent)
            {
                Advance(); // consume newline
                body = ParseIndentedBlock();
            }
            else if (Match(type: TokenType.Pass))
            {
                body = new PassStatement(Location: GetLocation());
            }
            else
            {
                body = ParseStatement();
            }
        }
        else
        {
            Consume(type: TokenType.FatArrow, errorMessage: "Expected '=>' after when pattern");
            body = ParseStatement();
        }

        _inWhenClauseBody = false;
        return body;
    }

    /// <summary>
    /// Parses a comptime arm-expansion inside a <c>when</c>:
    /// <c>expand m in branchof(T)</c> then an indented template clause <c>is ${m.type} x => body</c>
    /// (or payload-less <c>is ${m.type} => body</c>). Unrolled per variant arm at monomorphization.
    /// </summary>
    private WhenArmExpansion ParseWhenArmExpansion()
    {
        Consume(type: TokenType.Expand, errorMessage: "Expected 'expand'");
        string handle = ConsumeIdentifier(errorMessage: "Expected expand handle name");
        Consume(type: TokenType.In, errorMessage: "Expected 'in' in expand");
        // Name-agnostic: the source intrinsic (a when-expand's is always `branchof`) is read as a plain
        // identifier — the parser does not know the BuilderExpansion intrinsic names.
        ConsumeIdentifier(errorMessage: "Expected 'branchof' after 'in' in a when-expand");
        Consume(type: TokenType.LeftParen, errorMessage: "Expected '(' after 'branchof'");
        TypeExpression sourceType = ParseType();
        Consume(type: TokenType.RightParen, errorMessage: "Expected ')' after branchof type");
        Consume(type: TokenType.Newline, errorMessage: "Expected newline after branchof(...)");

        if (!Check(type: TokenType.Indent))
        {
            throw ThrowParseError(code: GrammarDiagnosticCode.ExpectedIndentedBlock,
                message: "Expected indented 'is ${...} => ...' clause after branchof(...)");
        }

        ProcessIndentToken();
        while (Match(TokenType.Newline, TokenType.DocComment)) { /* skip interleaved blank lines and doc comments */ }

        SourceLocation clauseLoc = GetLocation();
        Consume(type: TokenType.Is,
            errorMessage: "Expected 'is $typeof(m) ...' clause in an branchof-expand");
        // The arm pattern's type is the handle's arm type — the brace-less `$typeof(m)` splice (or the
        // legacy `${m.type}`); parsed and validated, then the optional payload binding follows.
        ParseAndValidateArmExpansionSplice(handle: handle);

        string? binding = Check(type: TokenType.Identifier)
            ? ConsumeIdentifier(errorMessage: "Expected payload binding name")
            : null;
        var pattern = new SpliceTypePattern(HandleName: handle, VariableName: binding,
            Location: clauseLoc);

        Statement body = ParseWhenClauseBody();
        Match(TokenType.Comma, TokenType.Newline);

        if (Check(type: TokenType.Dedent))
        {
            ProcessDedentTokens();
        }

        var template = new WhenClause(Pattern: pattern, Body: body, Location: clauseLoc);
        return new WhenArmExpansion(HandleName: handle, SourceType: sourceType, Template: template);
    }

    /// <summary>
    /// Parses and validates the arm-expansion type splice after <c>is</c>: either the brace-less
    /// <c>$typeof(m)</c> form or the legacy <c>${m.type}</c> form. Both must reference the given
    /// <paramref name="handle"/>; otherwise an <c>InvalidPattern</c> error is raised.
    /// </summary>
    private void ParseAndValidateArmExpansionSplice(string handle)
    {
        if (Match(type: TokenType.Dollar))
        {
            SpliceExpression splice = ParseDollarSplice(kind: SpliceKind.Value);
            if (splice.Inner is not CallExpression
                {
                    Callee: IdentifierExpression { Name: "typeof" },
                    Arguments: [IdentifierExpression spliceHandleNew]
                } || spliceHandleNew.Name != handle)
            {
                throw ThrowParseError(code: GrammarDiagnosticCode.InvalidPattern,
                    message: $"An branchof-expand arm pattern must be 'is $typeof({handle}) ...'.");
            }
        }
        else
        {
            Consume(type: TokenType.SpliceOpen,
                errorMessage: "Expected '$typeof(m)' type splice after 'is'");
            SpliceExpression splice = ParseSplice(kind: SpliceKind.Value);
            if (splice.Inner is not MemberExpression { Object: IdentifierExpression spliceHandle, MemberName: "type" }
                || spliceHandle.Name != handle)
            {
                throw ThrowParseError(code: GrammarDiagnosticCode.InvalidPattern,
                    message: $"An branchof-expand arm pattern must be 'is $typeof({handle}) ...'.");
            }
        }
    }

    /// <summary>
    /// Parses a flags pattern in a when clause after 'is' when the next tokens
    /// indicate a flags chain (identifier followed by and/or/but).
    /// Examples: is READ and WRITE =&gt; ..., is READ or WRITE =&gt; ...
    /// </summary>
    private FlagsPattern ParseFlagsIsWhenPattern()
    {
        SourceLocation loc = GetLocation();
        var flags = new List<string>();
        flags.Add(item: ConsumeIdentifier(errorMessage: "Expected flag name after 'is'"));

        FlagsTestConnective connective = FlagsTestConnective.And;
        List<string>? excluded = null;

        if (Check(type: TokenType.And))
        {
            while (Match(type: TokenType.And))
            {
                flags.Add(item: ConsumeIdentifier(errorMessage: ExpectedFlagNameAfterAnd));
            }

            if (Match(type: TokenType.But))
            {
                excluded = ParseExcludedFlagsAfterBut();
            }
        }
        else if (Check(type: TokenType.Or))
        {
            connective = FlagsTestConnective.Or;
            while (Match(type: TokenType.Or))
            {
                flags.Add(item: ConsumeIdentifier(errorMessage: "Expected flag name after 'or'"));
            }
        }
        else if (Match(type: TokenType.But))
        {
            excluded = ParseExcludedFlagsAfterBut();
        }

        return new FlagsPattern(FlagNames: flags,
            Connective: connective,
            ExcludedFlags: excluded,
            Location: loc);
    }

    /// <summary>
    /// Parses the excluded-flag list following a <c>but</c> keyword (already consumed): the first flag
    /// name, then each subsequent <c>and</c>-separated flag name.
    /// </summary>
    private List<string> ParseExcludedFlagsAfterBut()
    {
        var excluded = new List<string>
        {
            ConsumeIdentifier(errorMessage: "Expected flag name after 'but'")
        };
        while (Match(type: TokenType.And))
        {
            excluded.Add(item: ConsumeIdentifier(errorMessage: ExpectedFlagNameAfterAnd));
        }

        return excluded;
    }

    /// <summary>
    /// Parses a pattern for use in when clauses.
    /// Supports: wildcard (_), type patterns (Type varName), variant patterns (Type.CASE or CASE),
    /// destructuring patterns (CASE (a, b)), literal patterns, guard patterns (n if n &lt; 0).
    /// </summary>
    /// <returns>A <see cref="Pattern"/> AST node.</returns>
    private Pattern ParsePattern()
    {
        SourceLocation location = GetLocation();

        // Wildcard pattern: _
        if (Check(type: TokenType.Identifier) && CurrentToken.Text == "_")
        {
            Advance(); // consume the '_'
            Pattern wildcardPattern = new WildcardPattern(Location: location);
            return TryParseAndGuard(innerPattern: wildcardPattern, guardAllowed: true,
                location: location);
        }

        // Type/Variant pattern: Type, Type varName, Choice.CASE, Variant.CASE varName, CASE, CASE (a, b)
        if (Check(type: TokenType.Identifier) && CurrentToken.Text.Length > 0)
        {
            return ParseTypeOrVariantPattern(location: location);
        }

        // Literal pattern: constants like 42, "hello", true, etc. No flags collision → guard allowed.
        Expression expr = ParsePrimary();
        if (expr is LiteralExpression literal)
        {
            Pattern litPattern = new LiteralPattern(Value: literal.Value,
                LiteralType: literal.LiteralType,
                Location: location);
            return TryParseAndGuard(innerPattern: litPattern, guardAllowed: true,
                location: location);
        }

        // Otherwise, treat as expression pattern
        return new ExpressionPattern(Expression: expr, Location: location);
    }

    /// <summary>
    /// Parses a bare type/variant pattern that starts with an identifier (not the <c>is</c>-prefixed
    /// form): <c>Type</c>, <c>Type varName</c>, <c>Choice.CASE</c>, <c>Variant.CASE varName</c>,
    /// <c>CASE</c>, or <c>CASE (a, b)</c>, with an optional trailing <c>and</c>-guard.
    /// </summary>
    private Pattern ParseTypeOrVariantPattern(SourceLocation location)
    {
        string name = CurrentToken.Text;
        Advance();

        // Check for qualified name: Type.CASE or Type.CASE.SubCase
        name = ReadQualifiedPatternName(head: name);

        // Check for destructuring: Type.CASE (memberVar1, memberVar2), (memberVar: alias), or ((x, y), z)
        List<DestructuringBinding>? bindings = null;
        if (Match(type: TokenType.LeftParen))
        {
            bindings = ParseDestructuringBindingList();
            Consume(type: TokenType.RightParen,
                errorMessage: "Expected ')' after destructuring bindings");
        }

        // Check for variable binding (only if no destructuring)
        string? variableName = null;
        if (bindings == null && Check(type: TokenType.Identifier))
        {
            variableName =
                ConsumeIdentifier(errorMessage: "Expected variable name for type pattern");
        }

        TypeExpression type = new(Name: name, GenericArguments: null, Location: location);
        Pattern typePattern = new TypePattern(Type: type,
            VariableName: variableName,
            Bindings: bindings,
            Location: location);
        return TryParseAndGuard(innerPattern: typePattern,
            guardAllowed: variableName != null || bindings != null, location: location);
    }

    /// <summary>
    /// Reads a dot-qualified pattern name (<c>Type.CASE</c>, <c>Type.CASE.SubCase</c>) starting from an
    /// already-consumed <paramref name="head"/> identifier, appending each <c>.identifier</c> segment.
    /// </summary>
    private string ReadQualifiedPatternName(string head)
    {
        var nameSb = new System.Text.StringBuilder(head);
        while (Match(type: TokenType.Dot))
        {
            if (Match(type: TokenType.Identifier))
            {
                nameSb.Append('.');
                nameSb.Append(PeekToken(offset: -1).Text);
            }
            else
            {
                throw ThrowParseError(
                    code: GrammarDiagnosticCode.ExpectedDotInQualifiedPattern,
                    message: "Expected identifier after '.' in pattern");
            }
        }

        return nameSb.ToString();
    }

    /// <summary>
    /// Parses a type pattern (used after 'is' keyword).
    /// Syntax: Type, Type varName, Type.CASE, Type.CASE varName, CASE (a, b)
    /// Also handles special keywords like 'none'.
    /// </summary>
    /// <returns>A <see cref="TypePattern"/> AST node.</returns>
    private Pattern ParseTypePattern()
    {
        SourceLocation location = GetLocation();

        // Handle 'is None' as a special case - None is a keyword
        if (Match(type: TokenType.None))
        {
            return ParseNoneTypePattern(location: location);
        }

        if (!Check(type: TokenType.Identifier))
        {
            throw ThrowParseError(code: GrammarDiagnosticCode.ExpectedPattern,
                message: $"Expected type name after 'is', got {CurrentToken.Type}");
        }

        string name = CurrentToken.Text;
        Advance();

        // Check for qualified name: Type.CASE or Type.CASE.SubCase
        name = ReadQualifiedPatternName(head: name);

        // Generic-instance arm: `is Dict[Text, SerialValue] inner` / `is List[SerialValue] xs`.
        // Parse the type arguments so the pattern resolves to the concrete instance (and the binding
        // gets that full type) — mirrors ParseBaseType's `[...]` handling. Without this, a generic
        // variant arm can't be matched in `when` at all (parser stops at '[').
        List<TypeExpression>? genericArguments = null;
        if (Match(type: TokenType.LeftBracket))
        {
            genericArguments = new List<TypeExpression>();
            do
            {
                genericArguments.Add(item: ParseTypeOrConstGeneric());
            } while (Match(type: TokenType.Comma));

            Consume(type: TokenType.RightBracket,
                errorMessage: "Expected ']' after type arguments in pattern");
        }

        // Check for destructuring: Type.CASE (memberVar1, memberVar2), (memberVar: alias), or ((x, y), z)
        List<DestructuringBinding>? bindings = null;
        if (Match(type: TokenType.LeftParen))
        {
            bindings = ParseDestructuringBindingList();
            Consume(type: TokenType.RightParen,
                errorMessage: "Expected ')' after destructuring bindings");
        }

        // Check for variable binding (only if no destructuring)
        string? variableName = null;
        if (bindings == null && Check(type: TokenType.Identifier))
        {
            variableName =
                ConsumeIdentifier(errorMessage: "Expected variable name for type pattern");
        }

        var type = new TypeExpression(Name: name, GenericArguments: genericArguments, Location: location);
        Pattern typePattern = new TypePattern(Type: type,
            VariableName: variableName,
            Bindings: bindings,
            Location: location);
        return TryParseAndGuard(innerPattern: typePattern,
            guardAllowed: variableName != null || bindings != null, location: location);
    }

    /// <summary>
    /// Parses the <c>is None</c> type pattern after the <c>None</c> keyword has been consumed. <c>None</c>
    /// carries no payload, so a trailing binding (<c>is None x</c>) or destructuring (<c>is None (x, y)</c>)
    /// is rejected; it takes no <c>and</c>-guard either.
    /// </summary>
    private TypePattern ParseNoneTypePattern(SourceLocation location)
    {
        var noneType =
            new TypeExpression(Name: "None", GenericArguments: null, Location: location);
        // `None` carries no payload, so it binds nothing: reject a binding (`is None x`) or a
        // destructuring (`is None (x, y)`) after it.
        if ((Check(type: TokenType.Identifier) && !IsKeywordToken(token: CurrentToken)) ||
            Check(type: TokenType.LeftParen))
        {
            throw ThrowParseError(code: GrammarDiagnosticCode.InvalidPattern,
                message:
                "The 'None' pattern binds no value — remove the binding or destructuring after 'None'.");
        }

        // `is None` binds nothing, so it takes no `and`-guard (guards require a binding).
        return new TypePattern(Type: noneType,
            VariableName: null,
            Bindings: null,
            Location: location);
    }

    /// <summary>
    /// Tries to parse an <c>and</c>-guard after a pattern: <c>is T n and &lt;guard&gt;</c>,
    /// <c>== 0 and y &gt; 0</c>. The guard is any boolean expression and may reference the pattern's
    /// binding (if any) or outer variables. <paramref name="guardAllowed"/> gates whether a guard is
    /// syntactically unambiguous here: a bare type pattern (<c>is T</c> with no binding) is NOT
    /// allowed a guard because <c>is T and …</c> collides with a flags-test chain
    /// (<c>is FLAG_A and FLAG_B</c>); a bound type pattern, comparison, literal, or wildcard has no
    /// such collision. <c>is None</c> takes no guard.
    /// </summary>
    private Pattern TryParseAndGuard(Pattern innerPattern, bool guardAllowed, SourceLocation location)
    {
        if (!guardAllowed || !Match(type: TokenType.And))
        {
            return innerPattern;
        }

        // Reset _inWhenClauseBody so ParseEquality/ParseComparison parse the full guard expression
        // (needed for nested whens, where the clause-body flag would otherwise stop the parse early).
        bool savedInWhenClauseBody = _inWhenClauseBody;
        _inWhenClauseBody = false;
        Expression guard = ParseExpression();
        _inWhenClauseBody = savedInWhenClauseBody;
        return new GuardPattern(InnerPattern: innerPattern, Guard: guard, Location: location);
    }

    /// <summary>
    /// Checks if the given token type is a comparison operator used in when patterns.
    /// Supported operators: ==, !=, &lt;, &gt;, &lt;=, &gt;=
    /// </summary>
    /// <param name="tokenType">The token type to check.</param>
    /// <returns>True if the token is a comparison operator for patterns.</returns>
    private static bool IsComparisonOperator(TokenType tokenType)
    {
        return tokenType is TokenType.Equal or TokenType.NotEqual or TokenType.Less
            or TokenType.Greater or TokenType.LessEqual or TokenType.GreaterEqual;
    }

    /// <summary>
    /// Parses a comparison pattern in a when clause.
    /// Syntax: <c>== value</c>, <c>!= value</c>, <c>&lt; value</c>, <c>&gt; value</c>,
    /// <c>&lt;= value</c>, <c>&gt;= value</c>
    /// </summary>
    /// <returns>A <see cref="ComparisonPattern"/> or <see cref="GuardPattern"/> AST node.</returns>
    private Pattern ParseComparisonPattern()
    {
        SourceLocation location = GetLocation();
        TokenType op = CurrentToken.Type;
        Advance(); // consume the operator

        // Parse the value to compare against
        // Set context flag to prevent lambda parsing
        _inWhenPatternContext = true;
        Expression value = ParsePrimary();

        // Allow member access and calls on the primary expression (e.g., Status.ACTIVE, get_user())
        while (Check(type: TokenType.Dot) || Check(type: TokenType.LeftParen))
        {
            if (Match(type: TokenType.Dot))
            {
                string memberName =
                    ConsumeIdentifier(errorMessage: "Expected member name after '.'");
                value = new MemberExpression(Object: value,
                    MemberName: memberName,
                    Location: GetLocation());
            }
            else if (Match(type: TokenType.LeftParen))
            {
                List<Expression> args = ParseArgumentList();
                Consume(type: TokenType.RightParen, errorMessage: "Expected ')' after arguments");
                value = new CallExpression(Callee: value,
                    Arguments: args,
                    Location: GetLocation());
            }
        }

        _inWhenPatternContext = false;

        // Comparison patterns (`< 0`, `== 5`) start with an operator, so `and` after the value is
        // unambiguously a guard (no flags collision). The guard typically tests outer variables.
        var pattern = new ComparisonPattern(Operator: op, Value: value, Location: location);
        return TryParseAndGuard(innerPattern: pattern, guardAllowed: true, location: location);
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // JUMP STATEMENTS
    // ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Parses a return statement.
    /// Syntax: <c>return</c> or <c>return expression</c>
    /// </summary>
    /// <returns>A <see cref="ReturnStatement"/> AST node.</returns>
    private ReturnStatement ParseReturnStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        Expression? value = null;
        // Check for both Newline and Dedent/RightBrace - either can follow a valueless return
        if (!Check(type: TokenType.Newline) && !Check(type: TokenType.Dedent) &&
            !Check(type: TokenType.RightBrace) && !IsAtEnd)
        {
            value = ParseExpression();
        }

        ConsumeStatementTerminator();

        return new ReturnStatement(Value: value, Location: location);
    }

    /// <summary>
    /// Parses a becomes statement (block result value).
    /// Syntax: <c>becomes expression</c>
    /// Used in multi-statement when/if branches to explicitly indicate the branch's result.
    /// </summary>
    /// <returns>A <see cref="BecomesStatement"/> AST node.</returns>
    private BecomesStatement ParseBecomesStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        // becomes requires an expression (unlike return which can be valueless)
        Expression value = ParseExpression();

        ConsumeStatementTerminator();

        return new BecomesStatement(Value: value, Location: location);
    }

    /// <summary>
    /// Parses a throw (fail) statement for error propagation.
    /// Syntax: <c>throw errorExpression</c>
    /// Used with Crashable types for error handling.
    /// </summary>
    /// <returns>A <see cref="ThrowStatement"/> AST node.</returns>
    private ThrowStatement ParseThrowStatement(bool isFatal)
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        // throw/pierce both require an error expression (a crashable-kind type). `pierce` (isFatal)
        // marks a fatal, uncatchable crash — no `!`, no try_/check_ variants; `throw` is a recoverable
        // failure that propagates.
        Expression error = ParseExpression();

        ConsumeStatementTerminator();

        return new ThrowStatement(Error: error, Location: location, IsFatal: isFatal);
    }

    /// <summary>
    /// Parses an absent statement (return none/null).
    /// Syntax: <c>absent</c>
    /// Indicates the function returns no value (for optional types).
    /// </summary>
    /// <returns>An <see cref="AbsentStatement"/> AST node.</returns>
    private AbsentStatement ParseAbsentStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        // absent takes no arguments
        ConsumeStatementTerminator();

        return new AbsentStatement(Location: location);
    }

    /// <summary>
    /// Parses a break statement for exiting loops.
    /// Syntax: <c>break</c>
    /// </summary>
    /// <returns>A <see cref="BreakStatement"/> AST node.</returns>
    private BreakStatement ParseBreakStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));
        ConsumeStatementTerminator();
        return new BreakStatement(Location: location);
    }

    /// <summary>
    /// Parses a continue statement for skipping to next loop iteration.
    /// Syntax: <c>continue</c>
    /// </summary>
    /// <returns>A <see cref="ContinueStatement"/> AST node.</returns>
    private ContinueStatement ParseContinueStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));
        ConsumeStatementTerminator();
        return new ContinueStatement(Location: location);
    }

    /// <summary>
    /// Parses a pass statement (empty statement placeholder).
    /// Syntax: <c>pass</c>
    /// Used as a placeholder in empty blocks or protocols.
    /// </summary>
    /// <returns>A <see cref="PassStatement"/> AST node.</returns>
    private PassStatement ParsePassStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));
        ConsumeStatementTerminator();
        return new PassStatement(Location: location);
    }

    /// <summary>
    /// Parses a discard statement (explicitly ignores a return value).
    /// Syntax: <c>discard routine_call()</c>
    /// Used to explicitly indicate that a routine's return value is intentionally ignored.
    /// </summary>
    /// <returns>A <see cref="DiscardStatement"/> AST node.</returns>
    private DiscardStatement ParseDiscardStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));
        Expression expression = ParseExpression();

        // Whether the discarded expression is actually a routine call is a SEMANTIC check, not a
        // grammatical one — the semantic verifier reports it (RF-S421). The parser just records the
        // discarded expression, whatever its shape.
        ConsumeStatementTerminator();
        return new DiscardStatement(Expression: expression, Location: location);
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // RESOURCE MANAGEMENT
    // ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Parses a using block for scoped resource management (P17).
    /// Syntax: <c>using expr as name</c> with indented body.
    /// Multi-resource: <c>using expr1 as name1, expr2 as name2</c> (desugars to nested blocks).
    /// </summary>
    /// <remarks>
    /// <code>
    /// using open("file.txt") as file
    ///   var content = file.read_all()
    ///   process(content)
    ///   # file is automatically closed at block exit
    /// </code>
    /// </remarks>
    /// <returns>A <see cref="UsingStatement"/> AST node (possibly nested for multi-resource).</returns>
    private Statement ParseUsingStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        // Parse resource-binding pairs: using expr as name [, expr as name]*
        var resources = new List<(Expression Resource, string Name)>();

        do
        {
            Expression resource = ParseExpression();
            Consume(type: TokenType.As,
                errorMessage: "Expected 'as' after resource expression in using block");
            string name = ConsumeIdentifier(errorMessage: "Expected binding name after 'as'");
            resources.Add(item: (resource, name));
        } while (Match(type: TokenType.Comma));

        // Parse the indented body
        Statement body = ParseBody();

        // Optional `fallback` block — the contention branch of a fallible `using`
        // (runs when the resource's non-blocking `try_enter()` fails to acquire; no
        // resource is held on that path). Single-resource only.
        //
        // `fallback` is a CONTEXTUAL keyword: it stays a normal identifier everywhere else
        // (it's a common variable name — e.g. `unwrap_or(fallback:)`). It is only the block
        // keyword here, recognised as an identifier `fallback` immediately followed by an
        // indented block right after a `using` body.
        Statement? fallbackBody = ParseOptionalUsingFallbackBlock(resourceCount: resources.Count);

        // Build nested UsingStatements from inside out (last resource is innermost).
        // `fallback` (single-resource only) attaches to the sole using.
        Statement result = body;
        for (int i = resources.Count - 1; i >= 0; i--)
        {
            result = new UsingStatement(Resource: resources[index: i].Resource,
                Name: resources[index: i].Name,
                Body: result,
                Location: location,
                FallbackBody: i == 0 ? fallbackBody : null);
        }

        return result;
    }

    /// <summary>
    /// Parses the optional contextual <c>fallback</c> block that may follow a <c>using</c> body. Rejects
    /// it on a multi-resource <c>using</c> (fallible acquisition binds exactly one resource). Returns the
    /// fallback body, or null when no fallback block is present.
    /// </summary>
    private Statement? ParseOptionalUsingFallbackBlock(int resourceCount)
    {
        if (!IsContextualFallbackBlock())
        {
            return null;
        }

        Advance(); // consume the `fallback` identifier
        if (resourceCount > 1)
            throw ThrowParseError(
                message: "'fallback' is only allowed on a single-resource 'using' " +
                         "(a fallible acquisition binds exactly one resource).");
        return ParseBody();
    }

    /// <summary>
    /// Recognises the contextual `fallback` block that may follow a `using` body: a bare
    /// identifier <c>fallback</c> immediately followed by an indented block. Requiring the
    /// following INDENT keeps a real identifier `fallback` used as an ordinary statement
    /// (rare, but legal) from being misread as the keyword.
    /// </summary>
    private bool IsContextualFallbackBlock()
    {
        if (CurrentToken is not { Type: TokenType.Identifier, Text: "fallback" })
            return false;
        // After `fallback` comes either INDENT directly, or NEWLINE then INDENT.
        return PeekToken(offset: 1).Type == TokenType.Indent
               || (PeekToken(offset: 1).Type == TokenType.Newline
                   && PeekToken(offset: 2).Type == TokenType.Indent);
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // RF-ONLY CONSTRUCTS
    // ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Parses a danger statement (unsafe memory operations block).
    /// RF-only construct guarded by <c>_language == Language.RazorForge</c>.
    /// Syntax: <c>danger</c> followed by indented body.
    /// </summary>
    /// <returns>A <see cref="DangerStatement"/> AST node.</returns>
    private DangerStatement ParseDangerStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        // 'danger' is tokenized as a single Danger token (including the '!')
        BlockStatement body = ParseIndentedBlock();

        return new DangerStatement(Body: body, Location: location);
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // BLOCK / BODY PARSING
    // ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Parses a block body using indentation (indent/dedent delimited).
    /// Both RazorForge and Suflae use indentation-based syntax.
    /// </summary>
    /// <returns>A <see cref="BlockStatement"/> AST node.</returns>
    private BlockStatement ParseBody()
    {
        return ParseIndentedBlock();
    }

    /// <summary>
    /// Parses an indented block of statements.
    /// Expects INDENT token, parses statements until DEDENT.
    /// </summary>
    /// <returns>A <see cref="BlockStatement"/> containing the parsed statements.</returns>
    private BlockStatement ParseIndentedBlock()
    {
        SourceLocation location = GetLocation();
        var statements = new List<Statement>();

        // Consume newline before indent (optional if we're already at Indent)
        if (Check(type: TokenType.Newline))
        {
            Advance(); // consume newline
        }
        else if (!Check(type: TokenType.Indent))
        {
            throw ThrowParseError(code: GrammarDiagnosticCode.ExpectedIndentedBlock,
                message: "Expected newline before indented block");
        }

        // Must have an indent token for a proper indented block
        if (!Check(type: TokenType.Indent))
        {
            throw ThrowParseError(code: GrammarDiagnosticCode.ExpectedIndentedBlock,
                message: "Expected indented block");
        }

        // Process the indent token
        ProcessIndentToken();

        // Parse statements until we hit a dedent
        while (!Check(type: TokenType.Dedent) && !IsAtEnd)
        {
            // Skip empty lines and doc comments (indentation handler doesn't emit
            // Dedent for comment-only lines, so doc comments at lower indent may appear here)
            if (Match(TokenType.Newline, TokenType.DocComment))
            {
                continue;
            }

            try
            {
                Statement stmt = ParseStatement();
                statements.Add(item: stmt);
            }
            catch (GrammarException ex)
            {
                // Statement-level recovery: record the error, resynchronize to the next
                // statement in THIS block, and keep parsing — one bad statement must not
                // discard the rest of the routine (or cascade through the rest of the file).
                _errors.Add(item: ex.Message);
                DiagnosticRenderer.Print(ex: ex, writer: Console.Error);
                SynchronizeWithinBlock();
            }
        }

        // Process dedent tokens
        if (Check(type: TokenType.Dedent))
        {
            ProcessDedentTokens();
        }
        else if (!IsAtEnd)
        {
            throw ThrowParseError(code: GrammarDiagnosticCode.ExpectedDedent,
                message: "Expected dedent to close indented block");
        }

        return new BlockStatement(Statements: statements, Location: location);
    }

    /// <summary>
    /// Parses an expression statement (expression followed by newline/terminator).
    /// Used for function calls, assignments, and other expressions at statement level.
    /// </summary>
    /// <returns>An <see cref="ExpressionStatement"/> AST node.</returns>
    private ExpressionStatement ParseExpressionStatement()
    {
        Expression expr = ParseExpression();
        ConsumeStatementTerminator();
        return new ExpressionStatement(Expression: expr, Location: expr.Location);
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // DESTRUCTURING
    // ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Parses record/entity destructuring in var bindings.
    /// Syntax: <c>var (memberVar, memberVar2) = expr</c> or <c>var (memberVar: alias, memberVar2: alias2) = expr</c>
    /// or nested: <c>var ((x, y), radius) = circle</c>
    /// Destructuring only works for types where ALL member variables are public.
    /// </summary>
    /// <returns>A <see cref="DestructuringStatement"/> AST node.</returns>
    private DestructuringStatement ParseDestructuringDeclaration()
    {
        SourceLocation
            location =
                GetLocation(token: PeekToken(offset: -2)); // -2 because we already consumed 'var'

        // Parse the destructuring pattern (reuse ParseDestructuringBindings from Expressions)
        List<DestructuringBinding> bindings = ParseDestructuringBindings();

        var pattern = new DestructuringPattern(Bindings: bindings, Location: location);

        // Expect '='
        Consume(type: TokenType.Assign, errorMessage: "Expected '=' in destructuring");

        // Parse the initializer expression
        Expression initializer = ParseExpression();

        ConsumeStatementTerminator();

        return new DestructuringStatement(Pattern: pattern,
            Initializer: initializer,
            Location: location);
    }
}
