using System.Text;
using SyntaxTree;

namespace Compiler.Parser;

/// <summary>
/// Reclassifies the parser's uniform <see cref="BracketAccessExpression"/> nodes into the existing
/// downstream node types (<see cref="IndexExpression"/>, <see cref="GenericMemberRoutineCallExpression"/>,
/// <see cref="GenericMemberExpression"/>).
///
/// The parser deliberately does NOT decide whether <c>foo[...]</c> is a generic instantiation or a
/// value index — it parses the bracket contents uniformly as EXPRESSIONS. This pass performs the
/// single, centralized classification, using ONLY the local shape of the bracket node (its Object,
/// its Args, whether a trailing call <c>(...)</c> is present, and the failable marker). Because the
/// re-emitted node types are exactly what the old deciding parser produced, every downstream
/// consumer is unchanged.
///
/// Classification (a node is GENERIC if ANY holds, else it is an INDEX):
/// <list type="bullet">
///   <item>a trailing call <c>(...)</c> is present (<c>CallArgs != null</c>);</item>
///   <item>the failable marker preceded the brackets (<c>foo![T](x)</c>);</item>
///   <item>more than one bracket argument (an index never carries a top-level comma);</item>
///   <item>any argument is type-only-shaped (a <c>/</c> projection, a nested generic bracket, or a
///         const-generic literal).</item>
/// </list>
///
/// Bracket arguments of a GENERIC node are converted to <see cref="TypeExpression"/> via
/// <see cref="ExpressionToTypeArg"/>. This mirrors how <c>ParseType</c>/<c>ParseTypeOrConstGeneric</c>
/// build type arguments (projection names flattened onto <c>/</c>, const-generic literals stored as
/// the literal text, nested brackets recursed).
/// </summary>
internal static class BracketReclassifyPass
{
    /// <summary>
    /// Reclassifies a single freshly-parsed bracket node. The node's Object and children are already
    /// fully parsed at this point, so no surrounding context is required.
    /// </summary>
    public static Expression Reclassify(BracketAccessExpression node)
    {
        // The Object may itself contain a nested bracket node that was already reclassified when it
        // was built (ParsePostfix reclassifies bottom-up), so no recursion into Object is needed.
        bool isGeneric = node.IsFailable
                         || node.CallArgs is not null
                         || node.Args.Count > 1
                         || node.Args.Any(predicate: IsTypeOnlyShaped);

        if (!isGeneric)
        {
            // Value index: single arg, no call, no failable marker, not type-shaped.
            return new IndexExpression(Object: node.Object,
                Index: node.Args[index: 0],
                Location: node.Location);
        }

        List<TypeExpression> typeArgs = node.Args
            .Select(selector: ExpressionToTypeArg)
            .ToList();

        // When the parser folded a `.member` into the Object (obj.MemberRoutine[T](...)), the receiver and
        // member name are recovered from that MemberExpression; otherwise this is a free reference
        // (foo[T](x)) whose Object is the identifier and whose member name is that identifier.
        Expression receiver;
        string memberName;
        if (node.Object is MemberExpression member)
        {
            receiver = member.Object;
            memberName = member.MemberName;
        }
        else if (node.Object is IdentifierExpression identifier)
        {
            receiver = identifier;
            memberName = identifier.Name;
        }
        else
        {
            // Fallback: a generic access on an arbitrary expression object. Preserve the object as
            // both receiver and (best-effort) member name source; the resolver keys on the receiver.
            receiver = node.Object;
            memberName = (node.Object as IdentifierExpression)?.Name ?? "";
        }

        if (node.CallArgs is not null)
        {
            return new GenericMemberRoutineCallExpression(Object: receiver,
                MemberRoutineName: memberName,
                TypeArguments: typeArgs,
                Arguments: node.CallArgs,
                IsMemoryOperation: node.IsFailable,
                Location: node.Location);
        }

        return new GenericMemberExpression(Object: receiver,
            MemberName: memberName,
            TypeArguments: typeArgs,
            Location: node.Location);
    }

    /// <summary>
    /// Returns true when the expression can ONLY be a type argument, never a value index — a
    /// projection (<c>S/Iter</c>), a nested generic bracket, or a const-generic literal shape.
    /// </summary>
    private static bool IsTypeOnlyShaped(Expression expr)
    {
        return expr switch
        {
            // `a/b` projection (associated-type access) — only meaningful as a type argument.
            BinaryExpression { Operator: BinaryOperator.TrueDivide } => true,
            // A nested generic instantiation used as a type argument (Array[U64, N]).
            GenericMemberExpression => true,
            GenericMemberRoutineCallExpression => true,
            _ => false
        };
    }

    /// <summary>
    /// Recursively converts a bracket-argument expression into the <see cref="TypeExpression"/> the
    /// resolver expects, mirroring <c>ParseType</c>/<c>ParseTypeOrConstGeneric</c>.
    /// </summary>
    public static TypeExpression ExpressionToTypeArg(Expression expr)
    {
        switch (expr)
        {
            case IdentifierExpression id:
                return new TypeExpression(Name: id.Name,
                    GenericArguments: null,
                    Location: id.Location);

            // Projection chain `a/b/c` -> flattened name "a/b/c" (mirrors ParseBaseType's slash path).
            case BinaryExpression { Operator: BinaryOperator.TrueDivide } bin:
                return new TypeExpression(Name: FlattenProjection(bin: bin),
                    GenericArguments: null,
                    Location: bin.Location);

            // Const-generic literal -> literal text as the type name (mirrors ParseTypeOrConstGeneric).
            case LiteralExpression lit:
                return new TypeExpression(Name: LiteralText(lit: lit),
                    GenericArguments: null,
                    Location: lit.Location);

            // Nested generic instantiation as a type argument, e.g. Array[U64, N].
            case GenericMemberExpression gme:
                return new TypeExpression(Name: gme.MemberName,
                    GenericArguments: gme.TypeArguments,
                    Location: gme.Location);

            case GenericMemberRoutineCallExpression gmc:
                return new TypeExpression(Name: gmc.MemberRoutineName,
                    GenericArguments: gmc.TypeArguments,
                    Location: gmc.Location);

            // A single-argument nested bracket (List[S64]) reclassifies to an IndexExpression when
            // seen in isolation; as a TYPE argument it is a nested generic instantiation. The Object
            // supplies the type name and the Index becomes the single (recursive) type argument.
            case IndexExpression idx:
                return NestedIndexToTypeArg(idx: idx);

            // Qualified type name a.b -> "a.b" (mirrors the dotted type path).
            case MemberExpression mem:
                return new TypeExpression(Name: QualifiedName(mem: mem),
                    GenericArguments: null,
                    Location: mem.Location);

            // Unary negation on a const-generic literal (e.g. FixedInt[-1]) -> "-<literal>".
            case UnaryExpression { Operator: UnaryOperator.Minus, Operand: LiteralExpression neg }:
                return new TypeExpression(Name: "-" + LiteralText(lit: neg),
                    GenericArguments: null,
                    Location: expr.Location);

            // A comptime type-position splice `${m.type}` as a (possibly nested) generic argument —
            // e.g. `hijacked_from[${m.type}]` / `blank[Hijacked[${m.type}]]`. Bracket contents parse as
            // EXPRESSIONS first, so the splice arrives as a SpliceExpression wrapping `m.type`; mirror
            // ParseBaseType's `${m.type}` handling by producing the SpliceHandle TypeExpression the
            // resolver expects (only `.type` is valid in a type position — other projections fall through
            // to the resolver's diagnostic).
            case SpliceExpression
            {
                Inner: MemberExpression { Object: IdentifierExpression spliceHandle, MemberName: "type" }
            } se:
                return new TypeExpression(Name: "splice",
                    GenericArguments: null,
                    Location: se.Location,
                    SpliceHandle: spliceHandle.Name);

            // The brace-less form of the same TYPE splice: `$typeof(m)` as a generic argument, e.g.
            // `hijacked_from[$typeof(m)]`. The splice wraps a `typeof(handle)` call; mirror the `${m.type}`
            // reclassification above by producing the SpliceHandle TypeExpression the resolver expects.
            case SpliceExpression
            {
                Inner: CallExpression
                {
                    Callee: IdentifierExpression { Name: "typeof" },
                    Arguments: [IdentifierExpression typeofHandle]
                }
            } seOf:
                return new TypeExpression(Name: "splice",
                    GenericArguments: null,
                    Location: seOf.Location,
                    SpliceHandle: typeofHandle.Name);

            // A comptime VALUE-position splice as a const-generic argument, e.g.
            // `Array[U8, ${max(T.data_size().byte_size(), 8)}]`. Unlike the `${m.type}` TYPE splice above,
            // the inner is a scalar comptime expression; carry it on ComptimeValue for the monomorphizer
            // to fold into a ConstGenericValueTypeInfo once the concrete type args are known.
            case SpliceExpression valueSplice:
                return new TypeExpression(Name: "splice_value",
                    GenericArguments: null,
                    Location: valueSplice.Location,
                    ComptimeValue: valueSplice.Inner);

            // A TypeExpression already (should not normally occur from bracket parsing) passes through.
            case TypeExpression te:
                return te;

            default:
                // Best-effort: produce an empty name so the resolver can surface a proper diagnostic.
                // All named expression types are handled by the cases above, so none remain here.
                return new TypeExpression(
                    Name: "",
                    GenericArguments: null,
                    Location: expr.Location);
        }
    }

    /// <summary>
    /// Converts a single-argument nested bracket (List[S64]) — seen in isolation it reclassifies to an
    /// IndexExpression, but as a TYPE argument it is a nested generic instantiation. The Object supplies
    /// the type name and the Index becomes the single (recursive) type argument.
    /// </summary>
    private static TypeExpression NestedIndexToTypeArg(IndexExpression idx)
    {
        string nestedName = idx.Object switch
        {
            IdentifierExpression nid => nid.Name,
            MemberExpression nmem => QualifiedName(mem: nmem),
            BinaryExpression { Operator: BinaryOperator.TrueDivide } nbin =>
                FlattenProjection(bin: nbin),
            _ => ""
        };
        return new TypeExpression(Name: nestedName,
            GenericArguments: [ExpressionToTypeArg(expr: idx.Index)],
            Location: idx.Location);
    }

    /// <summary>Flattens a `/`-chained projection expression into a slash-joined name string.</summary>
    private static string FlattenProjection(BinaryExpression bin)
    {
        var sb = new StringBuilder();
        AppendProjection(expr: bin, sb: sb);
        return sb.ToString();
    }

    private static void AppendProjection(Expression expr, StringBuilder sb)
    {
        switch (expr)
        {
            case BinaryExpression { Operator: BinaryOperator.TrueDivide } bin:
                AppendProjection(expr: bin.Left, sb: sb);
                sb.Append(value: '/');
                AppendProjection(expr: bin.Right, sb: sb);
                break;
            case IdentifierExpression id:
                sb.Append(value: id.Name);
                break;
            case MemberExpression mem:
                sb.Append(value: QualifiedName(mem: mem));
                break;
            default:
                // All named expression types are handled above; append nothing for unrecognized nodes.
                sb.Append(value: "");
                break;
        }
    }

    /// <summary>Builds a dotted qualified type name from a member-access expression.</summary>
    private static string QualifiedName(MemberExpression mem)
    {
        string prefix = mem.Object switch
        {
            IdentifierExpression id => id.Name,
            MemberExpression inner => QualifiedName(mem: inner),
            _ => ""
        };
        return prefix.Length == 0 ? mem.MemberName : $"{prefix}.{mem.MemberName}";
    }

    /// <summary>Returns the source text of a const-generic literal.</summary>
    private static string LiteralText(LiteralExpression lit)
    {
        return lit.Value?.ToString() ?? "";
    }
}
