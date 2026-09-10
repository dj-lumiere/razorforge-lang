using SyntaxTree;
using TypeModel.Types;

namespace TypeModel.Symbols;

/// <summary>
/// Information about a parameter in a routine.
/// </summary>
public sealed class ParamInfo
{
    /// <summary>The name of the parameter.</summary>
    public string Name { get; }

    /// <summary>The resolved type of the parameter.</summary>
    public TypeSymbol Type { get; }

    /// <summary>The default value expression, if any.</summary>
    public Expression? DefaultValue { get; init; }

    /// <summary>Whether this parameter has a default value.</summary>
    public bool HasDefaultValue => DefaultValue != null;

    /// <summary>The index of this parameter in the parameter list.</summary>
    public int Index { get; init; }

    /// <summary>Whether this parameter was declared as variadic (values...). Preserved after List[T] wrapping.</summary>
    public bool IsVariadicParam { get; init; }

    /// <summary>
    /// Initializes a new instance of the <see cref="ParamInfo"/> class.
    /// </summary>
    /// <param name="name">The name of the parameter.</param>
    /// <param name="type">The resolved type of the parameter.</param>
    public ParamInfo(string name, TypeSymbol type)
    {
        Name = name;
        Type = type;
    }

    /// <summary>
    /// Creates a copy with substituted type for generic resolution.
    /// </summary>
    /// <param name="newType">The new type to substitute.</param>
    /// <returns>A new <see cref="ParamInfo"/> with the substituted type.</returns>
    public ParamInfo WithSubstitutedType(TypeSymbol newType)
    {
        return new ParamInfo(name: Name, type: newType)
        {
            DefaultValue = DefaultValue, Index = Index, IsVariadicParam = IsVariadicParam
        };
    }
}
