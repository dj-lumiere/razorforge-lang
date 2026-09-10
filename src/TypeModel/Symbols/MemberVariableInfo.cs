using SyntaxTree;
using TypeModel.Types;

namespace TypeModel.Symbols;

/// <summary>
/// Information about a member variable in a record or entity.
/// </summary>
/// <remarks>
/// Field visibility uses the four-level system:
/// <list type="bullet">
/// <item>public - read/write from anywhere</item>
/// <item>published - public read, private write</item>
/// <item>internal - read/write within module</item>
/// <item>private - read/write within file</item>
/// </list>
/// </remarks>
public sealed class MemberVariableInfo
{
    /// <summary>The name of the member variable.</summary>
    public string Name { get; }

    /// <summary>The resolved type of the member variable.</summary>
    public TypeSymbol Type { get; }

    /// <summary>
    /// Visibility for the member variable (open, posted, secret).
    /// For posted member variables, read is open but write is secret.
    /// </summary>
    public VisibilityModifier Visibility { get; init; } = VisibilityModifier.Open;

    /// <summary>The index of this member variable within the containing type.</summary>
    public int Index { get; init; }

    /// <summary>Whether this member variable has a default value.</summary>
    public bool HasDefaultValue { get; init; }

    /// <summary>Suflae: true when this entity-reference field was declared OPTIONAL (`x: E?`), so it may
    /// hold a null Roamed handle (none). A non-nullable entity field (`x: E`) must never be none.</summary>
    public bool IsNullable { get; init; }

    /// <summary>Source location where this member variable is defined.</summary>
    public SourceLocation? Location { get; init; }

    /// <summary>The type that owns this member variable.</summary>
    public TypeSymbol? Owner { get; init; }

    /// <summary>
    /// Initializes a new instance of the <see cref="MemberVariableInfo"/> class.
    /// </summary>
    /// <param name="name">The name of the member variable.</param>
    /// <param name="type">The resolved type of the member variable.</param>
    public MemberVariableInfo(string name, TypeSymbol type)
    {
        Name = name;
        Type = type;
    }

    /// <summary>
    /// Creates a copy of this member variable with the type substituted for generic resolution.
    /// </summary>
    /// <param name="newType">The new type to substitute.</param>
    /// <returns>A new <see cref="MemberVariableInfo"/> with the substituted type.</returns>
    public MemberVariableInfo WithSubstitutedType(TypeSymbol newType)
    {
        return new MemberVariableInfo(name: Name, type: newType)
        {
            Visibility = Visibility,
            Index = Index,
            HasDefaultValue = HasDefaultValue,
            IsNullable = IsNullable,
            Location = Location,
            Owner = Owner
        };
    }
}
