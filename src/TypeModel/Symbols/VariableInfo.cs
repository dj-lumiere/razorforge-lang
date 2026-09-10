using SyntaxTree;
using TypeModel.Types;

namespace TypeModel.Symbols;

/// <summary>
/// Information about a variable in a scope.
/// </summary>
public sealed class VariableInfo
{
    /// <summary>The name of the variable.</summary>
    public string Name { get; }

    /// <summary>The resolved type of the variable.</summary>
    public TypeSymbol Type { get; }

    /// <summary>Whether this variable is modifiable.
    /// Presets are not modifiable (IsModifiable=false).
    /// All other variables are modifiable (IsModifiable=true).</summary>
    public bool IsModifiable { get; init; }

    /// <summary>Whether this is a preset (build-time constant).
    /// Presets are always frozen (IsModifiable=false) and must be initialized with constant expressions.</summary>
    public bool IsPreset { get; init; }

    /// <summary>Whether this is a <c>secret preset</c> — file-private: visible (inlinable) only inside
    /// the file that declares it, unlike a public preset which is part of the global prelude.</summary>
    public bool IsSecret { get; init; }

    /// <summary>Whether this is a Suflae module-level <c>global</c> (`global counter: S64 = 0`). Unlike a
    /// preset it is MUTABLE (IsModifiable=true) and NOT inlined — codegen emits one module-level LLVM
    /// <c>@global</c> with storage, and reads/writes load/store it. Suflae-only (RazorForge bans
    /// module-level mutable state).</summary>
    public bool IsGlobal { get; init; }

    /// <summary>The module this variable belongs to.</summary>
    public string? Module { get; init; }

    /// <summary>The module-qualified name (e.g., "Core.S8_MIN").</summary>
    public string QualifiedName => string.IsNullOrEmpty(value: Module)
        ? Name
        : $"{Module}.{Name}";

    /// <summary>Source location where this variable is defined.</summary>
    public SourceLocation? Location { get; init; }

    /// <summary>Suflae: true when this local holds a NULLABLE entity reference (`E?` — a Roamed[E]
    /// handle that may be a null/none handle), inferred from its initializer (a `none` literal or a
    /// read of a nullable field/local). Flow typing gates member access on such a variable until it
    /// has been null-checked (see Scope proven-non-null tracking).</summary>
    public bool IsNullable { get; init; }

    /// <summary>
    /// For preset declarations: the constant value expression to inline at use sites.
    /// Null for ordinary variables.
    /// </summary>
    public Expression? PresetValue { get; init; }

    /// <summary>
    /// True when this preset is a constant aggregate (<c>Array[T, N]</c> or <c>BitArray[N]</c>). Such
    /// presets are NOT inlined at use sites (which would rebuild the whole array per reference — the
    /// cause of the fun_bench OOM where an <c>Array[U16, 1000]</c> DPD table was reconstructed as a
    /// heap List on every call). Instead the identifier is kept so codegen emits ONE module-level
    /// <c>@preset.*</c> constant and indexes into it. Scalar <c>@llvm</c> presets stay inlined.
    /// </summary>
    public bool IsPresettableAggregate =>
        IsPreset && PresetValue is ListLiteralExpression && Type is RecordTypeSymbol record &&
        (record.GenericDefinition ?? record).BareName is "Array" or "BitArray";

    /// <summary>
    /// Initializes a new instance of the <see cref="VariableInfo"/> class.
    /// </summary>
    /// <param name="name">The name of the variable.</param>
    /// <param name="type">The resolved type of the variable.</param>
    public VariableInfo(string name, TypeSymbol type)
    {
        Name = name;
        Type = type;
    }
}
