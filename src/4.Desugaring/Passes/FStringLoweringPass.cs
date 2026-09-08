using Compiler.Instantiation;
using Compiler.Tokenizer;
using SyntaxTree;
using TypeModel.Types;

namespace Compiler.Desugaring.Passes;

/// <summary>
/// Lowers <see cref="InsertedTextExpression"/> f-strings to <c>represent</c>/<c>diagnose</c>
/// memberRoutine calls folded with <c>Text.add</c>.
/// Runs after <see cref="ExpressionLoweringPass"/> and before <see cref="OperatorLoweringPass"/>
/// in the per-file desugaring pipeline.
///
/// <para>Part conversion:</para>
/// <list type="bullet">
///   <item><c>TextPart("text")</c> ??<c>LiteralExpression("text")</c></item>
///   <item><c>ExpressionPart(e, null)</c> ??<c>e.represent()</c></item>
///   <item><c>ExpressionPart(e, "?")</c> ??<c>e.diagnose()</c></item>
///   <item><c>ExpressionPart(e, "=")</c> ??<c>"name=" + e.represent()</c></item>
///   <item><c>ExpressionPart(e, "=?")</c> ??<c>"name=" + e.diagnose()</c></item>
/// </list>
///
/// <para>Scope: per-file user/stdlib code via <see cref="Run"/>, plus synthesized variant
/// bodies via <see cref="RunOnVariantBodies"/>. After both run, no
/// <see cref="InsertedTextExpression"/> reaches codegen.</para>
/// </summary>
internal sealed class FStringLoweringPass(PostprocessingContext ctx) : AstRewriter
{
    public void Run(Program program)
        => BodyDispatch.RunOnProgram(program, lower: r => VisitStatement(r.Body));

    public void RunOnVariantBodies()
        => BodyDispatch.RunOnVariantBodies(ctx.VariantBodies, lower: (_, body) => VisitStatement(body));

    /// <summary>
    /// Lowers f-strings in instantiated generic routine bodies. Phase 7's
    /// <c>GenericMonomorphizationPass</c> populates <c>InstantiatedGenericBodies</c> AFTER
    /// the Phase 8 RunGlobal sweep has finished, so monomorphized represent/diagnose bodies
    /// keep their raw <see cref="InsertedTextExpression"/> nodes and trip the codegen guard
    /// ("Expression type not implemented: InsertedTextExpression"). Mirrors
    /// <c>OperatorLoweringPass.RunOnInstantiatedGenericBodies</c>.
    /// </summary>
    public void RunOnInstantiatedGenericBodies(
        Dictionary<string, MonomorphizedBody> instantiatedGenericBodies)
        => BodyDispatch.RunOnInstantiatedGenericBodies(
            instantiatedGenericBodies, lower: (_, entry) => VisitStatement(entry.Ast.Body));

    //  F-string lowering

    /// <summary>
    /// The only node this pass rewrites: an f-string becomes its <c>represent</c>/<c>diagnose</c> +
    /// <c>Text.add</c> chain. All structural recursion (statements, other expressions, nested f-strings
    /// inside parts) is supplied by <see cref="AstRewriter"/>.
    /// </summary>
    protected override Expression VisitInsertedText(InsertedTextExpression e) => LowerFString(ftext: e);

    /// <summary>
    /// Converts an <see cref="InsertedTextExpression"/> to a left-folded chain of
    /// <c>Text.add</c> calls interleaved with <c>represent</c>/<c>diagnose</c> calls.
    /// </summary>
    private Expression LowerFString(InsertedTextExpression ftext)
    {
        TypeInfo? textType = ctx.Registry.LookupType(name: "Text");
        SourceLocation loc = ftext.Location;

        // Collect lowered Text expressions for each part (skipping empty text parts).
        var exprs = new List<Expression>(capacity: ftext.Parts.Count);
        foreach (InsertedTextPart part in ftext.Parts)
        {
            switch (part)
            {
                case TextPart { Text.Length: > 0 } tp:
                    exprs.Add(new LiteralExpression(
                        Value: tp.Text, LiteralType: TokenType.TextLiteral, Location: tp.Location)
                        { ResolvedType = textType });
                    break;

                case ExpressionPart ep:
                    AppendExpressionPart(exprs: exprs, ep: ep, textType: textType);
                    break;
            }
        }

        if (exprs.Count == 0)
        {
            return new LiteralExpression(
                Value: "", LiteralType: TokenType.TextLiteral, Location: loc)
                { ResolvedType = textType };
        }

        // Left-fold: acc = acc.add(other: next)
        Expression result = exprs[0];
        for (int i = 1; i < exprs.Count; i++)
        {
            result = new CallExpression(
                Callee: new MemberExpression(
                    Object: result,
                    MemberName: "add",
                    Location: loc),
                Arguments:
                [
                    new NamedArgumentExpression(
                        Name: "other",
                        Value: exprs[i],
                        Location: loc)
                ],
                Location: loc) { ResolvedType = textType };
        }

        return result;
    }

    /// <summary>
    /// Lowers a single <see cref="ExpressionPart"/> of an f-string, appending the resulting Text
    /// expression(s) to <paramref name="exprs"/>: an optional <c>"name="</c> literal (for the
    /// <c>=</c>/<c>=?</c> format specs) followed by the <c>represent</c>/<c>diagnose</c> render call.
    /// </summary>
    private void AppendExpressionPart(List<Expression> exprs, ExpressionPart ep, TypeInfo? textType)
    {
        Expression loweredInner = VisitExpression(ep.Expression);
        string memberRoutineName = ep.FormatSpec is "?" or "=?"
            ? Declaration.RuntimeContract.Display.Diagnose
            : Declaration.RuntimeContract.Display.Represent;

        // "=" and "=?" format specs prepend "varName=" as a text literal.
        if (ep.FormatSpec is "=" or "=?")
        {
            string varName = ep.Expression is IdentifierExpression id ? id.Name : "";
            if (varName.Length > 0)
            {
                exprs.Add(new LiteralExpression(
                    Value: varName + "=",
                    LiteralType: TokenType.TextLiteral,
                    Location: ep.Location) { ResolvedType = textType });
            }
        }

        Expression renderCall = new CallExpression(
            Callee: new MemberExpression(
                Object: loweredInner,
                MemberName: memberRoutineName,
                Location: ep.Location),
            Arguments: [],
            Location: ep.Location) { ResolvedType = textType };

        // In-flight entity values (`T`) inject `?` immediately before the
        // short type name in the rendered output, so a value of type
        // `Module.Counter` renders as `Module.?Counter(...)`. The rendered
        // text is post-processed via `Text.replace` because the type-name
        // prefix is compile-time known and appears verbatim at the head of
        // `diagnose` / `represent` output.
        if (ep.Expression is { IsInFlight: true, ResolvedType: EntityTypeInfo entityType })
        {
            renderCall = WrapInFlightEntityMarker(renderCall: renderCall, ep: ep,
                entityType: entityType, textType: textType);
        }

        exprs.Add(renderCall);
    }

    /// <summary>
    /// Post-processes an in-flight entity's rendered text via <c>Text.replace</c>, inserting a
    /// <c>?</c> immediately before the short type name in the compile-time-known type-name prefix.
    /// </summary>
    private static Expression WrapInFlightEntityMarker(Expression renderCall, ExpressionPart ep,
        EntityTypeInfo entityType, TypeInfo? textType)
    {
        string fullName = entityType.FullName;
        int dot = fullName.LastIndexOf('.');
        string marked = dot < 0
            ? "?" + fullName
            : fullName[..(dot + 1)] + "?" + fullName[(dot + 1)..];
        return new CallExpression(
            Callee: new MemberExpression(
                Object: renderCall,
                MemberName: Declaration.RuntimeContract.Collection.Replace,
                Location: ep.Location) { ResolvedType = textType },
            Arguments:
            [
                new NamedArgumentExpression(Name: "old",
                    Value: new LiteralExpression(Value: fullName,
                        LiteralType: TokenType.TextLiteral,
                        Location: ep.Location) { ResolvedType = textType },
                    Location: ep.Location),
                new NamedArgumentExpression(Name: "new",
                    Value: new LiteralExpression(Value: marked,
                        LiteralType: TokenType.TextLiteral,
                        Location: ep.Location) { ResolvedType = textType },
                    Location: ep.Location)
            ],
            Location: ep.Location) { ResolvedType = textType };
    }
}
