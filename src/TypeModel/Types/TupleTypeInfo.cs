using SyntaxTree;
using TypeModel.Symbols;

namespace TypeModel.Types;

/// <summary>
/// Type information for tuple types. Tuples are synthesized record types with
/// fields named item0, item1, ..., itemN. <c>(S64, Bool)</c> becomes
/// <c>Tuple[S64, Bool]</c> — a named LLVM struct type, emitted on demand.
/// </summary>
public sealed class TupleTypeInfo : RecordTypeInfo
{
    /// <summary>
    /// The element types in order (item0, item1, ..., itemN).
    /// </summary>
    public List<TypeInfo> ElementTypes { get; }

    /// <summary>
    /// <c>true</c> when all elements are record-like (records, choices, flags, variants,
    /// nested ValueTuples) — copy is a plain memcpy with no RC operations.<br/>
    /// <c>false</c> when at least one element is entity-like — copy/drop must manage RC.
    /// </summary>
    public bool IsValueTuple { get; }

    /// <summary>
    /// Returns <c>true</c> if <paramref name="t"/> is record-like for tuple inference purposes.
    /// </summary>
    public static bool IsRecordLike(TypeInfo t) => t switch
    {
        TupleTypeInfo tt => tt.IsValueTuple,
        RecordTypeInfo  => true, // includes VariantTypeInfo (a RecordTypeInfo subclass)
        _ => false
    };

    /// <summary>Creates a tuple type whose element types are the supplied list.</summary>
    public TupleTypeInfo(List<TypeInfo> elementTypes) : base(
        name: BuildName(elementTypes: elementTypes))
    {
        ElementTypes = elementTypes;
        IsValueTuple = elementTypes.All(predicate: IsRecordLike);
        Module = "Core";

        var memberVariables = new List<MemberVariableInfo>(capacity: elementTypes.Count);
        for (int i = 0; i < elementTypes.Count; i++)
        {
            memberVariables.Add(
                item: new MemberVariableInfo(name: $"item{i}", type: elementTypes[index: i])
                {
                    Visibility = VisibilityModifier.Open, Index = i
                });
        }

        MemberVariables = memberVariables;
        TypeArguments = elementTypes;
    }

    internal static string BuildName(List<TypeInfo> elementTypes)
    {
        string args = string.Join(separator: ", ",
            values: elementTypes.Select(selector: t => t.Name));
        return $"Tuple[{args}]";
    }

 
    /// <inheritdoc/>
    public override TypeInfo CreateInstance(List<TypeInfo> typeArguments)
    {
        throw new InvalidOperationException(
            "Tuple types cannot be further resolved. Create a new TupleTypeInfo instead.");
    }

    /// <summary>Gets the member variable info for a specific element index.</summary>
    public MemberVariableInfo? GetField(int index)
    {
        return index >= 0 && index < MemberVariables.Count
            ? MemberVariables[index: index]
            : null;
    }

    /// <summary>Gets the member variable info by name (item0, item1, etc.).</summary>
    public MemberVariableInfo? GetField(string memberVariableName)
    {
        if (!memberVariableName.StartsWith(value: "item",
                comparisonType: StringComparison.Ordinal))
        {
            return null;
        }

        if (int.TryParse(s: memberVariableName.AsSpan(start: 4), result: out int index))
        {
            return GetField(index: index);
        }

        return null;
    }

    /// <summary>The number of elements in this tuple.</summary>
    public int Arity => ElementTypes.Count;
}
