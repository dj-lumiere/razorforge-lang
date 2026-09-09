using SyntaxTree;
using TypeModel.Enums;

namespace TypeModel.Types;

/// <summary>
/// A const-generic argument whose integer value is a COMPTIME expression over the enclosing type's
/// generic parameters — e.g. the <c>${max(T.data_size().byte_size(), 8)}</c> in
/// <c>Array[U8, ${…}]</c> used by the Result/Lookup carrier payload buffer.
///
/// While the enclosing type is still generic (<c>T</c> unbound) this stays symbolic — it reports as
/// "contains a generic parameter" so the layout is not emitted. At monomorphization
/// <c>RoutineInfo.SubstituteType</c> calls <see cref="TryFold"/> with the concrete substitution
/// (<c>T → S64</c>, …) to collapse it into a plain <see cref="ConstGenericValueTypeInfo"/>.
///
/// The comptime grammar is deliberately tiny (only what the carrier buffer needs): integer literals,
/// <c>max</c>/<c>min</c>, a type's <c>.data_size()</c> (its byte size), and the identity
/// <c>ByteSize.byte_size()</c> accessor.
/// </summary>
public sealed class ComptimeConstGenericTypeInfo : TypeInfo
{
    /// <inheritdoc/>
    public override TypeCategory Category => TypeCategory.ConstGenericValue;

    /// <summary>The unevaluated comptime scalar expression (from a <c>${…}</c> type-position splice).</summary>
    public Expression ComptimeExpr { get; }

    /// <summary>Creates a symbolic comptime const-generic wrapping <paramref name="comptimeExpr"/>.</summary>
    public ComptimeConstGenericTypeInfo(Expression comptimeExpr) : base(name: "${comptime}")
    {
        ComptimeExpr = comptimeExpr;
    }

    /// <inheritdoc/>
    public override TypeInfo CreateInstance(List<TypeInfo> typeArguments)
    {
        throw new InvalidOperationException(
            message: "Cannot instantiate an unresolved comptime const-generic.");
    }

    /// <inheritdoc/>
    public override int SizeBytes(int pointerSize)
    {
        return 8;
    }

    /// <summary>
    /// Attempts to fold the comptime expression to a concrete integer given a type-parameter resolver
    /// (name → bound concrete type, or null if still unbound). Returns false (keep symbolic) when a
    /// referenced type is not yet concrete. The delegate form lets both substitution maps
    /// (<c>&lt;string, TypeSymbol&gt;</c> and <c>&lt;string, TypeInfo&gt;</c>) drive the fold.
    /// </summary>
    public bool TryFold(Func<string, TypeInfo?> resolveTypeParam, int pointerSize, out long result)
    {
        return TryEval(expr: ComptimeExpr,
            resolve: resolveTypeParam,
            pointerSize: pointerSize,
            result: out result);
    }

    private static bool TryEval(Expression expr, Func<string, TypeInfo?> resolve, int pointerSize,
        out long result)
    {
        result = 0;
        switch (expr)
        {
            case LiteralExpression lit:
                return TryParseIntLiteral(text: lit.Value?.ToString(), value: out result);

            case NamedArgumentExpression named:
                return TryEval(expr: named.Value,
                    resolve: resolve,
                    pointerSize: pointerSize,
                    result: out result);

            // max(a, b) / min(a, b)
            case CallExpression { Callee: IdentifierExpression { Name: "max" or "min" } fn } call
                when call.Arguments.Count == 2:
            {
                if (!TryEval(expr: call.Arguments[index: 0],
                        resolve: resolve,
                        pointerSize: pointerSize,
                        result: out long a) || !TryEval(expr: call.Arguments[index: 1],
                        resolve: resolve,
                        pointerSize: pointerSize,
                        result: out long b))
                {
                    return false;
                }

                result = fn.Name == "max"
                    ? Math.Max(val1: a, val2: b)
                    : Math.Min(val1: a, val2: b);
                return true;
            }

            // <type>.data_size()  →  byte size of the concrete type;  <expr>.byte_size()  →  identity
            case CallExpression { Callee: MemberExpression member } call
                when call.Arguments.Count == 0:
            {
                if (member.MemberName == "byte_size")
                {
                    return TryEval(expr: member.Object,
                        resolve: resolve,
                        pointerSize: pointerSize,
                        result: out result);
                }

                if (member.MemberName == "data_size" &&
                    member.Object is IdentifierExpression typeRef &&
                    resolve(arg: typeRef.Name) is { } boundType &&
                    boundType is not GenericParameterTypeInfo &&
                    boundType is not ComptimeConstGenericTypeInfo)
                {
                    result = boundType.SizeBytes(pointerSize: pointerSize);
                    return true;
                }

                return false;
            }

            default:
                return false;
        }
    }

    private static bool TryParseIntLiteral(string? text, out long value)
    {
        value = 0;
        if (string.IsNullOrEmpty(value: text))
        {
            return false;
        }

        // Strip a trailing type suffix (e.g. "8u64", "8_s32") and separator underscores.
        int end = 0;
        while (end < text!.Length &&
               (char.IsDigit(c: text[index: end]) || text[index: end] == '-'))
        {
            end++;
        }

        string digits = text[..end]
           .Replace(oldValue: "_", newValue: "");
        return long.TryParse(s: digits, result: out value);
    }
}
