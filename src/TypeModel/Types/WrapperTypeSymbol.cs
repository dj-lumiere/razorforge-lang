using TypeModel.Enums;

namespace TypeModel.Types;

/// <summary>
/// Builder-synthesized wrapper types (Viewing, Modifying, Retained, Tracked, Guarded, Witnessed, Consulting, Amending, Hijacked).
/// These types transparently forward member access to their inner type while providing
/// ownership and access control semantics.
/// </summary>
public sealed class WrapperTypeSymbol : TypeSymbol
{
    /// <inheritdoc/>
    public override TypeCategory Category => TypeCategory.Wrapper;

    /// <summary>The inner type being wrapped (T in Wrapper&lt;T&gt;).</summary>
    public TypeSymbol InnerType { get; }

    /// <summary>Whether this is a read-only wrapper (Viewing, Consulting).</summary>
    public bool IsReadOnly { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="WrapperTypeSymbol"/> class.
    /// </summary>
    /// <param name="wrapperName">The name of the wrapper type (e.g., "Modifying", "Viewing").</param>
    /// <param name="innerType">The type being wrapped.</param>
    /// <param name="isReadOnly">Whether this is a read-only wrapper.</param>
    public WrapperTypeSymbol(string wrapperName, TypeSymbol innerType, bool isReadOnly = false) : base(
        name: wrapperName)
    {
        InnerType = innerType;
        IsReadOnly = isReadOnly;
        TypeArguments = [innerType];
    }


    /// <inheritdoc/>
    public override TypeSymbol CreateInstance(List<TypeSymbol> typeArguments)
    {
        if (typeArguments.Count != 1)
        {
            throw new InvalidOperationException(
                message: $"Wrapper type '{Name}' requires exactly one type argument.");
        }

        return new WrapperTypeSymbol(wrapperName: Name,
            innerType: typeArguments[index: 0],
            isReadOnly: IsReadOnly) { Module = Module, Realm = Realm };
    }

    /// <summary>
    /// Well-known wrapper type definitions.
    /// These are used as templates for creating resolved wrapper types.
    /// </summary>
    public static class WellKnown
    {
        /// <summary>
        /// Read-only single-threaded wrapper. Provides unmodifiable view of the inner value.
        /// </summary>
        public static readonly WrapperTypeSymbol ViewingDefinition = new(
            wrapperName: Builder.Declaration.RuntimeContract.Viewing,
            innerType: ErrorTypeSymbol.Instance, // Placeholder, will be resolved with actual type
            isReadOnly: true) { GenericParameters = ["T"], Module = "Core" };

        /// <summary>
        /// Exclusive-write single-threaded wrapper. Provides modifiable access with exclusive ownership.
        /// </summary>
        public static readonly WrapperTypeSymbol ModifyingDefinition = new(
            wrapperName: Builder.Declaration.RuntimeContract.Modifying,
            innerType: ErrorTypeSymbol.Instance,
            isReadOnly: false) { GenericParameters = ["T"], Module = "Core" };

        /// <summary>
        /// Read-only multi-threaded wrapper. Thread-safe unmodifiable view.
        /// </summary>
        public static readonly WrapperTypeSymbol ConsultingDefinition = new(
            wrapperName: Builder.Declaration.RuntimeContract.Consulting,
            innerType: ErrorTypeSymbol.Instance,
            isReadOnly: true) { GenericParameters = ["T"], Module = "Core" };

        /// <summary>
        /// Exclusive-write multi-threaded wrapper. Thread-safe modifiable access with exclusive ownership.
        /// </summary>
        public static readonly WrapperTypeSymbol AmendingDefinition = new(
            wrapperName: Builder.Declaration.RuntimeContract.Amending,
            innerType: ErrorTypeSymbol.Instance,
            isReadOnly: false) { GenericParameters = ["T"], Module = "Core" };

        /// <summary>
        /// Reference-counted single-threaded handle. Guarded ownership with automatic cleanup.
        /// </summary>
        public static readonly WrapperTypeSymbol RetainedDefinition = new(
            wrapperName: Builder.Declaration.RuntimeContract.Retained,
            innerType: ErrorTypeSymbol.Instance,
            isReadOnly: false) { GenericParameters = ["T"], Module = "Core" };

        /// <summary>
        /// Weak single-threaded handle. Non-owning reference that can become invalid.
        /// </summary>
        public static readonly WrapperTypeSymbol TrackedWeakDefinition = new(
            wrapperName: Builder.Declaration.RuntimeContract.Tracked,
            innerType: ErrorTypeSymbol.Instance,
            isReadOnly: false) { GenericParameters = ["T"], Module = "Core" };

        /// <summary>
        /// Reference-counted wrapper. Guarded ownership with automatic cleanup.
        /// </summary>
        public static readonly WrapperTypeSymbol SharedDefinition = new(
            wrapperName: Builder.Declaration.RuntimeContract.Guarded,
            innerType: ErrorTypeSymbol.Instance,
            isReadOnly: false) { GenericParameters = ["T"], Module = "Core" };

        /// <summary>
        /// Weak-reference wrapper. Non-owning reference that can become invalid.
        /// </summary>
        public static readonly WrapperTypeSymbol WatchedDefinition = new(
            wrapperName: Builder.Declaration.RuntimeContract.Witnessed,
            innerType: ErrorTypeSymbol.Instance,
            isReadOnly: false) { GenericParameters = ["T"], Module = "Core" };

        /// <summary>
        /// Unsafe raw-pointer wrapper. Danger zone only.
        /// </summary>
        public static readonly WrapperTypeSymbol HijackedDefinition = new(
            wrapperName: Builder.Declaration.RuntimeContract.Hijacked,
            innerType: ErrorTypeSymbol.Instance,
            isReadOnly: false) { GenericParameters = ["T"], Module = "Core" };

        /// <summary>All well-known wrapper type definitions.</summary>
        public static IEnumerable<WrapperTypeSymbol> All =>
        [
            ViewingDefinition,
            ModifyingDefinition,
            RetainedDefinition,
            TrackedWeakDefinition,
            ConsultingDefinition,
            AmendingDefinition,
            SharedDefinition,
            WatchedDefinition,
            HijackedDefinition
        ];
    }
}
