using TypeModel.Enums;

namespace TypeModel.Types;

/// <summary>
/// Builder-synthesized wrapper types (Viewing, Modifying, Retained, Tracked, Guarded, Witnessed, Consulting, Amending, Hijacked).
/// These types transparently forward member access to their inner type while providing
/// ownership and access control semantics.
/// </summary>
public sealed class WrapperTypeInfo : TypeInfo
{
    /// <inheritdoc/>
    public override TypeCategory Category => TypeCategory.Wrapper;

    /// <summary>The inner type being wrapped (T in Wrapper&lt;T&gt;).</summary>
    public TypeInfo InnerType { get; }

    /// <summary>Whether this is a read-only wrapper (Viewing, Consulting).</summary>
    public bool IsReadOnly { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="WrapperTypeInfo"/> class.
    /// </summary>
    /// <param name="wrapperName">The name of the wrapper type (e.g., "Modifying", "Viewing").</param>
    /// <param name="innerType">The type being wrapped.</param>
    /// <param name="isReadOnly">Whether this is a read-only wrapper.</param>
    public WrapperTypeInfo(string wrapperName, TypeInfo innerType, bool isReadOnly = false) : base(
        name: wrapperName)
    {
        InnerType = innerType;
        IsReadOnly = isReadOnly;
        TypeArguments = [innerType];
    }


    /// <inheritdoc/>
    public override TypeInfo CreateInstance(List<TypeInfo> typeArguments)
    {
        if (typeArguments.Count != 1)
        {
            throw new InvalidOperationException(
                message: $"Wrapper type '{Name}' requires exactly one type argument.");
        }

        return new WrapperTypeInfo(wrapperName: Name,
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
        public static readonly WrapperTypeInfo ViewingDefinition = new(
            wrapperName: Compiler.Declaration.RuntimeContract.Viewing,
            innerType: ErrorTypeInfo.Instance, // Placeholder, will be resolved with actual type
            isReadOnly: true) { GenericParameters = ["T"], Module = "Core" };

        /// <summary>
        /// Exclusive-write single-threaded wrapper. Provides modifiable access with exclusive ownership.
        /// </summary>
        public static readonly WrapperTypeInfo ModifyingDefinition = new(
            wrapperName: Compiler.Declaration.RuntimeContract.Modifying,
            innerType: ErrorTypeInfo.Instance,
            isReadOnly: false) { GenericParameters = ["T"], Module = "Core" };

        /// <summary>
        /// Read-only multi-threaded wrapper. Thread-safe unmodifiable view.
        /// </summary>
        public static readonly WrapperTypeInfo ConsultingDefinition = new(
            wrapperName: Compiler.Declaration.RuntimeContract.Consulting,
            innerType: ErrorTypeInfo.Instance,
            isReadOnly: true) { GenericParameters = ["T"], Module = "Core" };

        /// <summary>
        /// Exclusive-write multi-threaded wrapper. Thread-safe modifiable access with exclusive ownership.
        /// </summary>
        public static readonly WrapperTypeInfo AmendingDefinition = new(
            wrapperName: Compiler.Declaration.RuntimeContract.Amending,
            innerType: ErrorTypeInfo.Instance,
            isReadOnly: false) { GenericParameters = ["T"], Module = "Core" };

        /// <summary>
        /// Reference-counted single-threaded handle. Guarded ownership with automatic cleanup.
        /// </summary>
        public static readonly WrapperTypeInfo RetainedDefinition = new(
            wrapperName: Compiler.Declaration.RuntimeContract.Retained,
            innerType: ErrorTypeInfo.Instance,
            isReadOnly: false) { GenericParameters = ["T"], Module = "Core" };

        /// <summary>
        /// Weak single-threaded handle. Non-owning reference that can become invalid.
        /// </summary>
        public static readonly WrapperTypeInfo TrackedWeakDefinition = new(
            wrapperName: Compiler.Declaration.RuntimeContract.Tracked,
            innerType: ErrorTypeInfo.Instance,
            isReadOnly: false) { GenericParameters = ["T"], Module = "Core" };

        /// <summary>
        /// Reference-counted wrapper. Guarded ownership with automatic cleanup.
        /// </summary>
        public static readonly WrapperTypeInfo SharedDefinition = new(
            wrapperName: Compiler.Declaration.RuntimeContract.Guarded,
            innerType: ErrorTypeInfo.Instance,
            isReadOnly: false) { GenericParameters = ["T"], Module = "Core" };

        /// <summary>
        /// Weak-reference wrapper. Non-owning reference that can become invalid.
        /// </summary>
        public static readonly WrapperTypeInfo WatchedDefinition = new(
            wrapperName: Compiler.Declaration.RuntimeContract.Witnessed,
            innerType: ErrorTypeInfo.Instance,
            isReadOnly: false) { GenericParameters = ["T"], Module = "Core" };

        /// <summary>
        /// Unsafe raw-pointer wrapper. Danger zone only.
        /// </summary>
        public static readonly WrapperTypeInfo HijackedDefinition = new(
            wrapperName: Compiler.Declaration.RuntimeContract.Hijacked,
            innerType: ErrorTypeInfo.Instance,
            isReadOnly: false) { GenericParameters = ["T"], Module = "Core" };

        /// <summary>All well-known wrapper type definitions.</summary>
        public static IEnumerable<WrapperTypeInfo> All =>
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
