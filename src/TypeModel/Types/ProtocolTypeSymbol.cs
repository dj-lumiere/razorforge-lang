using TypeModel.Enums;
using TypeModel.Symbols;

namespace TypeModel.Types;

/// <summary>
/// Type information for protocols (interface/trait definitions).
/// Protocols define contracts that types can implement via the `obeys` keyword.
/// </summary>
public sealed class ProtocolTypeSymbol : TypeSymbol
{
    /// <inheritdoc/>
    public override TypeCategory Category => TypeCategory.Protocol;

    /// <summary>memberRoutine signatures defined by this protocol.</summary>
    public List<ProtocolMemberRoutineInfo> MemberRoutines { get; set; } = [];

    /// <summary>Parent protocols that this protocol extends.</summary>
    public List<ProtocolTypeSymbol> ParentProtocols { get; init; } = [];

    /// <summary>
    /// Associated-type slots declared by this protocol via <c>relates Name obeys Constraint</c>.
    /// Implementers bind these (see <see cref="EntityTypeSymbol.AssociatedTypeBindings"/>).
    /// </summary>
    public List<AssociatedTypeSlot> AssociatedTypes { get; set; } = [];

    /// <summary>
    /// For generic definitions, the original generic type this was resolved from.
    /// </summary>
    public ProtocolTypeSymbol? GenericDefinition { get; init; }

    /// <summary>
    /// Initializes a new instance of the <see cref="ProtocolTypeSymbol"/> class.
    /// </summary>
    /// <param name="name">The name of the protocol.</param>
    public ProtocolTypeSymbol(string name) : base(name: name)
    {
    }

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">Thrown if this is not a generic definition.</exception>
    /// <exception cref="ArgumentException">Thrown if the number of type arguments doesn't match.</exception>
    public override TypeSymbol CreateInstance(List<TypeSymbol> typeArguments)
    {
        if (!IsGenericDefinition)
        {
            throw new InvalidOperationException(
                message: $"Protocol '{Name}' is not a generic definition.");
        }

        if (typeArguments.Count != GenericParameters!.Count)
        {
            throw new ArgumentException(
                message:
                $"Expected {GenericParameters.Count} type arguments, got {typeArguments.Count}.");
        }

        // Create type parameter substitution map
        var substitution = new Dictionary<string, TypeSymbol>();
        for (int i = 0; i < GenericParameters.Count; i++)
        {
            substitution[key: GenericParameters[index: i]] = typeArguments[index: i];
        }

        // Build resolved type name
        string resolvedName = $"{Name}[{string.Join(separator: ", ",
            values: typeArguments.Select(selector: t => t.Name))}]";

        var substitutedMemberRoutines = MemberRoutines.Select(selector: m =>
                                                           new ProtocolMemberRoutineInfo(
                                                               name: m.Name)
                                                           {
                                                               IsInstanceMemberRoutine =
                                                                   m.IsInstanceMemberRoutine,
                                                               Mutation = m.Mutation,
                                                               ParameterTypes = m.ParameterTypes
                                                                  .Select(selector: t =>
                                                                       RecordTypeSymbol
                                                                          .SubstituteType(type: t,
                                                                               substitution:
                                                                               substitution))
                                                                  .ToList(),
                                                               ParameterNames = m.ParameterNames,
                                                               ReturnType = m.ReturnType != null
                                                                   ? RecordTypeSymbol.SubstituteType(
                                                                       type: m.ReturnType,
                                                                       substitution: substitution)
                                                                   : null,
                                                               IsFailable = m.IsFailable,
                                                               GenerationKind = m.GenerationKind,
                                                               HasDefaultImplementation =
                                                                   m.HasDefaultImplementation,
                                                               IsAutoDerivedVariant =
                                                                   m.IsAutoDerivedVariant,
                                                               Location = m.Location
                                                           })
                                                      .ToList();

        var substitutedParentProtocols = ParentProtocols.Select(selector: p =>
                                                             (ProtocolTypeSymbol)RecordTypeSymbol
                                                                .SubstituteType(type: p,
                                                                     substitution: substitution))
                                                        .ToList();

        // Substitute the protocol's own generic params into each slot's constraint
        // (e.g. Iterable[T]'s `Iter obeys Iterator[T]` becomes `Iter obeys Iterator[Text]`).
        var substitutedAssociated = AssociatedTypes.Select(selector: s =>
                                                        new AssociatedTypeSlot(name: s.Name)
                                                        {
                                                            Constraint = s.Constraint != null
                                                                ? RecordTypeSymbol.SubstituteType(
                                                                    type: s.Constraint,
                                                                    substitution: substitution)
                                                                : null
                                                        })
                                                   .ToList();

        return new ProtocolTypeSymbol(name: resolvedName)
        {
            MemberRoutines = substitutedMemberRoutines,
            ParentProtocols = substitutedParentProtocols,
            AssociatedTypes = substitutedAssociated,
            TypeArguments = typeArguments,
            GenericDefinition = this,
            Visibility = Visibility,
            Location = Location,
            Module = Module,
            Realm = Realm
        };
    }
}
