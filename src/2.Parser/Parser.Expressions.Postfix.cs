using Compiler.Diagnostics;
using Compiler.Tokenizer;
using SyntaxTree;

namespace Compiler.Parser;

/// <summary>
/// Partial class containing postfix expression parsing.
/// </summary>
public partial class Parser
{
    private const string ExpectedRightParenAfterArguments = "Expected ')' after arguments";

    private Expression ParsePostfix()
    {
        Expression expr = ParsePrimary();

        while (true)
        {
            if (!TryParsePostfixStep(ref expr))
            {
                break;
            }
        }

        return expr;
    }

    /// <summary>
    /// Attempts to parse one postfix step on <paramref name="expr"/>, updating it in place.
    /// Returns <c>true</c> when a postfix operator was consumed and the loop should try another;
    /// <c>false</c> when no postfix token was found and parsing should stop.
    /// </summary>
    private bool TryParsePostfixStep(ref Expression expr)
    {
        // Bracket access: expr[...], expr[...](...), or expr![...](...)
        if (IsBracketAccessStart())
        {
            expr = HandleBracketAccess(expr: expr);
            return true;
        }

        // Failable call: identifier!(args)
        if (IsFailableCallStart())
        {
            expr = HandleFailableCall(expr: expr);
            return true;
        }

        // Plain call: expr(args)
        if (Match(type: TokenType.LeftParen))
        {
            List<Expression> args = ParseArgumentList();
            Consume(type: TokenType.RightParen, errorMessage: ExpectedRightParenAfterArguments);
            expr = new CallExpression(Callee: expr, Arguments: args, Location: expr.Location);
            return true;
        }

        // Optional chaining: obj?.member
        if (Match(type: TokenType.QuestionDot))
        {
            string member = ConsumeMemberRoutineName(errorMessage: "Expected member name after '?.'");
            expr = new OptionalMemberExpression(Object: expr, MemberName: member,
                Location: expr.Location);
            return true;
        }

        // Comptime splice selectors: obj.${expr} or obj.$primary
        if (TryParseSpliceMember(expr: expr, result: out Expression? spliceMember))
        {
            expr = spliceMember;
            return true;
        }

        // Plain member access: obj.member
        if (Match(type: TokenType.Dot))
        {
            expr = HandleMemberAccess(expr: expr).Expr;
            return true;
        }

        // Force unwrap: expr!! — extracts the value from Maybe[T], panics if None
        if (Match(type: TokenType.BangBang))
        {
            expr = new UnaryExpression(Operator: UnaryOperator.ForceUnwrap,
                Operand: expr, Location: expr.Location);
            return true;
        }

        // Multi-line dot chaining: consume pending newlines when the next non-newline is a dot
        if (Check(type: TokenType.Newline))
        {
            return TrySkipNewlinesBeforeDot();
        }

        return false;
    }

    /// <summary>Returns true when the current token sequence starts a bracket access expression.</summary>
    private bool IsBracketAccessStart() =>
        Check(type: TokenType.LeftBracket) ||
        (Check(type: TokenType.Bang) && PeekToken(offset: 1).Type == TokenType.LeftBracket);

    /// <summary>Returns true when the current token sequence starts a failable call expression.</summary>
    private bool IsFailableCallStart() =>
        Check(type: TokenType.Bang) && PeekToken(offset: 1).Type == TokenType.LeftParen;

    /// <summary>
    /// Attempts to parse a comptime splice member access if the current token is a dot followed by
    /// a splice opener (<c>${</c>) or a bare dollar (<c>$</c>). Returns false when no splice follows.
    /// </summary>
    private bool TryParseSpliceMember(Expression expr, out Expression? result)
    {
        if (Check(type: TokenType.Dot) && PeekToken(offset: 1).Type == TokenType.SpliceOpen)
        {
            // Comptime splice selector: obj.${expr}. A distinct SpliceMemberExpression so the
            // monomorphizer folds the splice to a concrete field name before SA member resolve.
            Advance(); // consume '.'
            Advance(); // consume '${'
            SpliceExpression selector = ParseSplice(kind: SpliceKind.Selector);
            result = new SpliceMemberExpression(Object: expr, Selector: selector,
                Location: expr.Location);
            return true;
        }

        if (Check(type: TokenType.Dot) && PeekToken(offset: 1).Type == TokenType.Dollar)
        {
            // Brace-less comptime splice selector: obj.$nameof(m). Same SpliceMemberExpression as
            // the braced form; the monomorphizer folds nameof(m) to the concrete field name.
            Advance(); // consume '.'
            Advance(); // consume '$'
            SpliceExpression selector = ParseDollarSplice(kind: SpliceKind.Selector);
            result = new SpliceMemberExpression(Object: expr, Selector: selector,
                Location: expr.Location);
            return true;
        }

        result = null;
        return false;
    }

    /// <summary>
    /// Handles uniform bracket access: <c>expr[...]</c>, <c>expr[...](...)</c>, <c>expr![...](...)</c>.
    /// The parser NO LONGER decides whether the brackets are a generic type-argument list or a value
    /// index. It parses the bracket contents uniformly as expressions and emits a
    /// BracketAccessExpression; the BracketReclassifyPass (run before the main semantic resolve)
    /// rewrites this into an IndexExpression / GenericMemberRoutineCallExpression / GenericMemberExpression.
    /// A failable <c>!</c> may precede the brackets (func![T](x)); it is recorded as
    /// BracketAccessExpression.IsFailable, never baked into a name.
    /// </summary>
    private Expression HandleBracketAccess(Expression expr)
    {
        bool isFailable = Match(type: TokenType.Bang);

        Advance(); // consume '['
        var bracketArgs = new List<Expression>();
        do
        {
            bracketArgs.Add(item: ParseBracketArg());
        } while (Match(type: TokenType.Comma));

        Consume(type: TokenType.RightBracket,
            errorMessage: "Expected ']' after bracket contents");

        List<Expression>? callArgs = null;
        if (Match(type: TokenType.LeftParen))
        {
            callArgs = ParseArgumentList();
            Consume(type: TokenType.RightParen,
                errorMessage: ExpectedRightParenAfterArguments);
        }

        // Slice syntax `xs[a til b]` IS supported: a single-arg no-call subscript whose index
        // is a RangeExpression stays an index access and lowers to `xs.getitem(range)` (a
        // `getitem(range: Range[...])` overload). Overload resolution by index-argument type
        // separates it from the scalar `getitem(index)`.
        var bracketNode = new BracketAccessExpression(Object: expr,
            Args: bracketArgs,
            CallArgs: callArgs,
            Location: expr.Location) { IsFailable = isFailable };
        return BracketReclassifyPass.Reclassify(node: bracketNode);
    }

    /// <summary>
    /// Handles a throwable function call: <c>identifier!(args)</c> with named arguments.
    /// </summary>
    private CallExpression HandleFailableCall(Expression expr)
    {
        Advance(); // consume '!'
        Advance(); // consume '('

        List<Expression> args = ParseArgumentList();
        Consume(type: TokenType.RightParen,
            errorMessage: ExpectedRightParenAfterArguments);

        if (expr is IdentifierExpression identExpr)
        {
            return new CallExpression(
                Callee: new IdentifierExpression(Name: identExpr.Name,
                    Location: identExpr.Location,
                    // Preserve the `::` realm qualifier on a failable foreign call
                    // (`C::rf_foo!(...)`) so the strict realm gate can see it.
                    Realm: identExpr.Realm),
                Arguments: args,
                Location: expr.Location) { IsFailable = true };
        }

        return new CallExpression(Callee: expr,
            Arguments: args,
            Location: expr.Location) { IsFailable = true };
    }

    /// <summary>
    /// Handles member access after a consumed <c>.</c>: plain member, member call, failable member call,
    /// and generic member access/call. Returns the new expression plus whether the postfix loop should
    /// <c>continue</c> (a generic bracket member call restarts the loop).
    /// </summary>
    private (Expression Expr, bool RestartLoop) HandleMemberAccess(Expression expr)
    {
        // Member access. The wired marker `$` (me.assign(), me.emit!()) is a separate Dollar
        // token — consume it here; the resolved routine's own IsWiredMemberRoutine carries the
        // wired attribute, and lookup keys on the BARE name, so the call name stays bare.
        Match(type: TokenType.Dollar);
        // Consume the bare member name WITHOUT folding a trailing `!` into it (unlike
        // ConsumeMemberRoutineName, which the declaration parser still uses): the `!` stays a separate
        // Bang token so the failable-call / generic-failable handling below records it as a
        // structured MemberExpression.IsFailable / GenericMemberRoutineCallExpression flag.
        if (!Check(type: TokenType.Identifier) &&
            !IsKeywordValidAsMemberRoutineName(CurrentToken.Type))
        {
            throw ThrowParseError(code: GrammarDiagnosticCode.ExpectedIdentifier,
                message: "Expected member name after '.'");
        }

        string member = CurrentToken.Text;
        Advance();

        // Generic member access / call: obj.MemberRoutine[T](...) or obj.MemberRoutine![T](...).
        // Parsed uniformly (no generic-vs-index decision): the `.MemberRoutine` folds into a
        // MemberExpression and the brackets attach as a BracketAccessExpression whose
        // Object is that MemberExpression. BracketReclassifyPass rewrites this into a
        // GenericMemberRoutineCallExpression / GenericMemberExpression. A `!` before the
        // brackets is the memory-op marker, recorded as BracketAccessExpression.IsFailable.
        if ((Check(type: TokenType.Bang) && PeekToken(offset: 1)
                .Type == TokenType.LeftBracket) ||
            Check(type: TokenType.LeftBracket))
        {
            return (HandleGenericMemberAccess(expr: expr, member: member), true);
        }

        return (HandlePlainMemberAccess(expr: expr, member: member), false);
    }

    /// <summary>
    /// Handles a generic member access/call: <c>obj.MemberRoutine[T](...)</c> or
    /// <c>obj.MemberRoutine![T](...)</c>. Called after the member name is consumed and a bracket
    /// (optionally preceded by <c>!</c>) is confirmed next.
    /// </summary>
    private Expression HandleGenericMemberAccess(Expression expr, string member)
    {
        bool isGenericMemOp = Match(type: TokenType.Bang);

        Advance(); // consume '['
        var bracketArgs = new List<Expression>();
        do
        {
            bracketArgs.Add(item: ParseBracketArg());
        } while (Match(type: TokenType.Comma));

        Consume(type: TokenType.RightBracket,
            errorMessage: "Expected ']' after bracket contents");

        List<Expression>? callArgs = null;
        if (Match(type: TokenType.LeftParen))
        {
            callArgs = ParseArgumentList();
            Consume(type: TokenType.RightParen,
                errorMessage: ExpectedRightParenAfterArguments);
        }

        Expression memberObj = new MemberExpression(Object: expr,
            MemberName: member,
            Location: expr.Location);
        var bracketNode = new BracketAccessExpression(Object: memberObj,
            Args: bracketArgs,
            CallArgs: callArgs,
            Location: expr.Location) { IsFailable = isGenericMemOp };
        return BracketReclassifyPass.Reclassify(node: bracketNode);
    }

    /// <summary>
    /// Handles a plain member access, a member call, or a failable member call after the member name is
    /// consumed: <c>obj.MemberRoutine</c>, <c>obj.MemberRoutine(args)</c>, <c>obj.MemberRoutine!(args)</c>.
    /// </summary>
    private Expression HandlePlainMemberAccess(Expression expr, string member)
    {
        // Regular member access
        // Check for failable memberRoutine call with ! suffix
        if (Match(type: TokenType.Bang) && Match(type: TokenType.LeftParen))
        {
            // Failable memberRoutine call: obj.MemberRoutine!(args)
            // Represented as CallExpression with MemberExpression callee
            List<Expression> args = ParseArgumentList();
            Consume(type: TokenType.RightParen,
                errorMessage: ExpectedRightParenAfterArguments);

            Expression memberExpr = new MemberExpression(Object: expr,
                MemberName: member,
                Location: expr.Location) { IsFailable = true };
            return new CallExpression(Callee: memberExpr,
                Arguments: args,
                Location: expr.Location);
        }

        if (Match(type: TokenType.LeftParen))
        {
            // Regular memberRoutine call: obj.MemberRoutine(args)
            // Represented as CallExpression with MemberExpression callee
            List<Expression> args = ParseArgumentList();
            Consume(type: TokenType.RightParen,
                errorMessage: ExpectedRightParenAfterArguments);

            Expression memberExpr = new MemberExpression(Object: expr,
                MemberName: member,
                Location: expr.Location);
            return new CallExpression(Callee: memberExpr,
                Arguments: args,
                Location: expr.Location);
        }

        return new MemberExpression(Object: expr,
            MemberName: member,
            Location: expr.Location);
    }

    /// <summary>Advances past all consecutive <see cref="TokenType.Newline"/> tokens.</summary>
    private void ConsumeNewlines()
    {
        while (Match(type: TokenType.Newline)) { /* advance past each newline */ }
    }

    /// <summary>
    /// Multi-line dot chaining: if the current newline(s) are followed by a dot, consume the newlines
    /// and report that the postfix loop should continue. Returns false (leaving the parser position
    /// unchanged) when the newlines are not followed by a dot.
    /// </summary>
    private bool TrySkipNewlinesBeforeDot()
    {
        int offset = 0;
        while (PeekToken(offset: offset)
                  .Type == TokenType.Newline)
        {
            offset++;
        }

        if (PeekToken(offset: offset)
               .Type == TokenType.Dot)
        {
            // Consume all pending newlines; the next postfix iteration will handle the dot.
            ConsumeNewlines();
            return true;
        }

        return false;
    }
}
