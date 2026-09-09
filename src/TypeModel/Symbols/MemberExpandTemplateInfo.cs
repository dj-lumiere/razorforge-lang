using SyntaxTree;
using TypeModel.Types;

namespace TypeModel.Symbols;

using TypeSymbol = TypeInfo;

/// <summary>
/// A resolved decl-position <c>expand</c> column template captured on a generic record/entity
/// definition. At instantiation the type registry materializes one <see cref="MemberVariableInfo"/>
/// per member of the concrete source type: the column name is <see cref="NamePrefix"/> + the field
/// name, and the column type is <see cref="ColumnTypeTemplate"/> with the per-field placeholder
/// (<see cref="ColumnPlaceholderName"/>) substituted by that field's static type.
/// </summary>
/// <remarks>
/// The struct-of-arrays layout of <c>SplitArray[T, N]</c> / <c>SplitList[T]</c> falls out of ordinary
/// record layout once these columns are appended as real member variables — no bespoke codegen.
/// </remarks>
public sealed class MemberExpandTemplateInfo
{
    /// <summary>The synthetic per-field placeholder a <c>${m.type}</c> splice resolves to during
    /// generic-def resolution; substituted by each concrete field's type at instantiation.</summary>
    public const string ColumnPlaceholderName = "0col";

    /// <summary>The literal name prefix (e.g. <c>"inner_"</c>) prepended to each field name, or "".</summary>
    public string NamePrefix { get; }

    /// <summary>The generic-parameter name whose members are iterated (the <c>T</c> in <c>allmemvarof(T)</c>).</summary>
    public string SourceParamName { get; }

    /// <summary>The column type with <see cref="ColumnPlaceholderName"/> standing in for the field type
    /// (e.g. <c>Array[$Col, N]</c> or <c>Hijacked[$Col]</c>).</summary>
    public TypeSymbol ColumnTypeTemplate { get; }

    /// <summary>Access modifier for the generated column member variables.</summary>
    public VisibilityModifier Visibility { get; }

    /// <summary>
    /// Initializes a new <see cref="MemberExpandTemplateInfo"/>.
    /// </summary>
    public MemberExpandTemplateInfo(string namePrefix, string sourceParamName,
        TypeSymbol columnTypeTemplate, VisibilityModifier visibility)
    {
        NamePrefix = namePrefix;
        SourceParamName = sourceParamName;
        ColumnTypeTemplate = columnTypeTemplate;
        Visibility = visibility;
    }
}
