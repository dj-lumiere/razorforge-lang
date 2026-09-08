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
            // ===============================================================================
            // CASE 1: Uniform bracket access - expr[...], expr[...](...), expr![...](...)
            // ===============================================================================
            if (Check(type: TokenType.LeftBracket) ||
                (Check(type: TokenType.Bang) && PeekToken(offset: 1).Type == TokenType.LeftBracket))
            {
                expr = HandleBracketAccess(expr: expr);
            }
            // Throwable function call: identifier!(args) with named arguments
            else if (Check(type: TokenType.Bang) && PeekToken(offset: 1)
                        .Type == TokenType.LeftParen)
            {
                expr = HandleFailableCall(expr: expr);
            }
            else if (Match(type: TokenType.LeftParen))
            {
                // FreeRoutine call - supports named arguments (name: value)
                List<Expression> args = ParseArgumentList();
                Consume(type: TokenType.RightParen,
                    errorMessage: ExpectedRightParenAfterArguments);
                expr = new CallExpression(Callee: expr, Arguments: args, Location: expr.Location);
            }
            else if (Match(type: TokenType.QuestionDot))
            {
                // Optional chaining: obj?.member
                string member = ConsumeMemberRoutineName(errorMessage: "Expected member name after '?.'");
                expr = new OptionalMemberExpression(Object: expr,
                    MemberName: member,
                    Location: expr.Location);
            }
            else if (Check(type: TokenType.Dot) &&
                     PeekToken(offset: 1).Type == TokenType.SpliceOpen)
            {
                // Comptime splice selector: obj.${expr}. Kept as a distinct SpliceMemberExpression
                // (never a plain MemberExpression) so the monomorphizer folds the splice to a
                // concrete field name and rewrites it to a real member access.
                Advance(); // consume '.'
                Advance(); // consume '${'
                SpliceExpression selector = ParseSplice(kind: SpliceKind.Selector);
                expr = new SpliceMemberExpression(Object: expr, Selector: selector,
                    Location: expr.Location);
            }
            else if (Check(type: TokenType.Dot) &&
                     PeekToken(offset: 1).Type == TokenType.Dollar)
            {
                // Brace-less comptime splice selector: obj.$nameof(m). Same SpliceMemberExpression as the
                // legacy obj.${m.name}; the monomorphizer folds nameof(m) to the concrete field name.
                Advance(); // consume '.'
                Advance(); // consume '$'
                SpliceExpression selector = ParseDollarSplice(kind: SpliceKind.Selector);
                expr = new SpliceMemberExpression(Object: expr, Selector: selector,
                    Location: expr.Location);
            }
            else if (Match(type: TokenType.Dot))
            {
                (Expression next, bool restartLoop) = HandleMemberAccess(expr: expr);
                expr = next;
                if (restartLoop)
                {
                    continue;
                }
            }
            // ===============================================================================
            // CASE 7: Force unwrap - expr!! (extract value from Maybe<T>, panic if None)
            // ===============================================================================
            else if (Match(type: TokenType.BangBang))
            {
                expr = new UnaryExpression(Operator: UnaryOperator.ForceUnwrap,
                    Operand: expr,
                    Location: expr.Location);
            }
            // ===============================================================================
            // CASE 8: Multi-line dot chaining - skip newlines if followed by a dot
            // Allows:  items
            //            .where(x => x > 0)
            //            .select(x => x * 2)
            // ===============================================================================
            else if (Check(type: TokenType.Newline))
            {
                if (TrySkipNewlinesBeforeDot())
                {
                    continue;
                }

                break;
            }
            else
            {
                break;
            }
        }

        return expr;
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
    private Expression HandleFailableCall(Expression expr)
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
            // Consume newlines and let next iteration handle the dot
            while (Match(type: TokenType.Newline))
            {
            } // NOSONAR S108: intentional newline-consuming loop

            return true;
        }

        return false;
    }
}
