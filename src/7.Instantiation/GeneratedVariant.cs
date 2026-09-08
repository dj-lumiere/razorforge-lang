using TypeModel.Symbols;

namespace Compiler.Instantiation;

/// <summary>
/// A generated error handling variant.
/// </summary>
/// <param name="Kind">The kind of variant.</param>
/// <param name="Routine">The generated routine info.</param>
public readonly record struct GeneratedVariant(ErrorHandlingVariantKind Kind, RoutineInfo Routine);
