namespace Compilers.Shared.AST;

/// <summary>
/// Well-known protocol/feature names used throughout the type system.
/// Types implement these protocols to gain specific capabilities.
/// </summary>
public static class WellKnownProtocols
{
    // Numeric protocols
    public const string Numeric = "Numeric";
    public const string SignedNumeric = "SignedNumeric";
    public const string Integer = "Integer";
    public const string SignedInteger = "SignedInteger";
    public const string UnsignedInteger = "UnsignedInteger";
    public const string FloatingPoint = "FloatingPoint";
    public const string DecimalFloatingPoint = "DecimalFloatingPoint";
    public const string BinaryFloatingPoint = "BinaryFloatingPoint";
    public const string FixedWidth = "FixedWidth";
    public const string Integral = "Integral";

    // Comparison and ordering protocols
    public const string Equatable = "Equatable";
    public const string Comparable = "Comparable";
    public const string Hashable = "Hashable";

    // String/text protocols
    public const string Parsable = "Parsable";
    public const string Printable = "Printable";
    public const string TextConvertible = "TextConvertible";

    // Memory and lifecycle protocols
    public const string Copyable = "Copyable";
    public const string Movable = "Movable";
    public const string Droppable = "Droppable";
    public const string DefaultConstructible = "DefaultConstructible";

    // Error handling protocols
    public const string Crashable = "Crashable";

    // Collection protocols
    public const string Iterable = "Iterable";
    public const string Indexable = "Indexable";
    public const string Collection = "Collection";
}

/// <summary>
/// Represents type information for expressions and symbols.
/// This is the canonical type representation used throughout the compiler pipeline.
/// </summary>
/// <param name="Name">The base type name (e.g., "s32", "Text", "cstr")</param>
/// <param name="IsReference">true if this is a reference type</param>
/// <param name="GenericArguments">List of generic type arguments if this is a generic type</param>
/// <param name="IsGenericParameter">true if this is a generic type parameter (e.g., T)</param>
/// <param name="Protocols">Set of protocols/features this type implements</param>
/// <remarks>
/// TypeInfo supports all RazorForge and Suflae types:
/// <list type="bullet">
/// <item>Primitive integers: s8, s16, s32, s64, s128, saddr, u8, u16, u32, u64, u128, uaddr</item>
/// <item>Floating point: f16, f32, f64, f128</item>
/// <item>Decimal: d32, d64, d128</item>
/// <item>Text types: text8, text16, text</item>
/// <item>User-defined records and entities</item>
/// <item>Generic types with type arguments</item>
/// </list>
/// Type capabilities are determined by the protocols they implement, not by name patterns.
/// </remarks>
public record TypeInfo(
    string Name,
    bool IsReference,
    List<TypeInfo>? GenericArguments = null,
    bool IsGenericParameter = false,
    HashSet<string>? Protocols = null)
{
    /// <summary>
    /// Checks if this type implements the specified protocol.
    /// </summary>
    /// <param name="protocol">The protocol name to check</param>
    /// <returns>true if the type implements the protocol</returns>
    public bool Implements(string protocol) => Protocols?.Contains(protocol) ?? false;

    /// <summary>true if this is any numeric type (implements Numeric protocol)</summary>
    public bool IsNumeric => Implements(WellKnownProtocols.Numeric);

    /// <summary>true if this is any integer type (implements Integer protocol)</summary>
    public bool IsInteger => Implements(WellKnownProtocols.Integer);

    /// <summary>true if this is any floating point type (implements FloatingPoint protocol)</summary>
    public bool IsFloatingPoint => Implements(WellKnownProtocols.FloatingPoint);

    /// <summary>true if this is a signed numeric type (implements SignedNumeric protocol)</summary>
    public bool IsSigned => Implements(WellKnownProtocols.SignedNumeric);

    /// <summary>true if this is an unsigned integer type (implements UnsignedInteger protocol)</summary>
    public bool IsUnsigned => Implements(WellKnownProtocols.UnsignedInteger);

    /// <summary>true if this type implements Equatable</summary>
    public bool IsEquatable => Implements(WellKnownProtocols.Equatable);

    /// <summary>true if this type implements Comparable</summary>
    public bool IsComparable => Implements(WellKnownProtocols.Comparable);

    /// <summary>true if this type implements Hashable</summary>
    public bool IsHashable => Implements(WellKnownProtocols.Hashable);

    /// <summary>true if this type implements Crashable (error handling)</summary>
    public bool IsCrashable => Implements(WellKnownProtocols.Crashable);

    /// <summary>true if this is a generic type (has generic arguments)</summary>
    public bool IsGeneric => GenericArguments is { Count: > 0 };

    /// <summary>Gets the fully qualified type name including generic arguments (e.g., "List&lt;s32>")</summary>
    public string FullName
    {
        get
        {
            if (!IsGeneric)
            {
                return Name;
            }

            string args = string.Join(separator: ", ",
                values: GenericArguments!.Select(selector: t => t.FullName));
            return $"{Name}<{args}>";
        }
    }

    /// <summary>
    /// Creates a new TypeInfo with the specified protocol added.
    /// </summary>
    public TypeInfo WithProtocol(string protocol)
    {
        var newProtocols = Protocols != null ? new HashSet<string>(Protocols) : new HashSet<string>();
        newProtocols.Add(protocol);
        return this with { Protocols = newProtocols };
    }

    /// <summary>
    /// Creates a new TypeInfo with multiple protocols added.
    /// </summary>
    public TypeInfo WithProtocols(IEnumerable<string> protocols)
    {
        var newProtocols = Protocols != null ? new HashSet<string>(Protocols) : new HashSet<string>();
        foreach (string p in protocols)
        {
            newProtocols.Add(p);
        }

        return this with { Protocols = newProtocols };
    }
}

/// <summary>
/// Factory for creating TypeInfo instances for built-in primitive types.
/// Ensures consistent protocol assignments for all primitive types.
/// </summary>
public static class PrimitiveTypes
{
    // Common protocol sets for reuse
    private static readonly HashSet<string> SignedIntegerProtocols = new()
    {
        WellKnownProtocols.Numeric,
        WellKnownProtocols.SignedNumeric,
        WellKnownProtocols.Integer,
        WellKnownProtocols.SignedInteger,
        WellKnownProtocols.Integral,
        WellKnownProtocols.FixedWidth,
        WellKnownProtocols.Equatable,
        WellKnownProtocols.Comparable,
        WellKnownProtocols.Hashable,
        WellKnownProtocols.Parsable,
        WellKnownProtocols.Printable,
        WellKnownProtocols.Copyable,
        WellKnownProtocols.DefaultConstructible
    };

    private static readonly HashSet<string> UnsignedIntegerProtocols = new()
    {
        WellKnownProtocols.Numeric,
        WellKnownProtocols.Integer,
        WellKnownProtocols.UnsignedInteger,
        WellKnownProtocols.Integral,
        WellKnownProtocols.FixedWidth,
        WellKnownProtocols.Equatable,
        WellKnownProtocols.Comparable,
        WellKnownProtocols.Hashable,
        WellKnownProtocols.Parsable,
        WellKnownProtocols.Printable,
        WellKnownProtocols.Copyable,
        WellKnownProtocols.DefaultConstructible
    };

    private static readonly HashSet<string> BinaryFloatProtocols = new()
    {
        WellKnownProtocols.Numeric,
        WellKnownProtocols.SignedNumeric,
        WellKnownProtocols.FloatingPoint,
        WellKnownProtocols.BinaryFloatingPoint,
        WellKnownProtocols.FixedWidth,
        WellKnownProtocols.Equatable,
        WellKnownProtocols.Comparable,
        WellKnownProtocols.Parsable,
        WellKnownProtocols.Printable,
        WellKnownProtocols.Copyable,
        WellKnownProtocols.DefaultConstructible
    };

    private static readonly HashSet<string> DecimalFloatProtocols = new()
    {
        WellKnownProtocols.Numeric,
        WellKnownProtocols.SignedNumeric,
        WellKnownProtocols.FloatingPoint,
        WellKnownProtocols.DecimalFloatingPoint,
        WellKnownProtocols.FixedWidth,
        WellKnownProtocols.Equatable,
        WellKnownProtocols.Comparable,
        WellKnownProtocols.Parsable,
        WellKnownProtocols.Printable,
        WellKnownProtocols.Copyable,
        WellKnownProtocols.DefaultConstructible
    };

    private static readonly HashSet<string> BoolProtocols = new()
    {
        WellKnownProtocols.Equatable,
        WellKnownProtocols.Hashable,
        WellKnownProtocols.Parsable,
        WellKnownProtocols.Printable,
        WellKnownProtocols.Copyable,
        WellKnownProtocols.DefaultConstructible
    };

    /// <summary>
    /// Gets or creates TypeInfo for a primitive type by name.
    /// For user-defined types, returns a basic TypeInfo without protocol information.
    /// </summary>
    public static TypeInfo GetTypeInfo(string typeName, bool isReference = false)
    {
        return typeName switch
        {
            // Signed integers
            "s8" or "s16" or "s32" or "s64" or "s128" or "saddr" =>
                new TypeInfo(typeName, isReference, Protocols: SignedIntegerProtocols),

            // Unsigned integers
            "u8" or "u16" or "u32" or "u64" or "u128" or "uaddr" =>
                new TypeInfo(typeName, isReference, Protocols: UnsignedIntegerProtocols),

            // Binary floating point
            "f16" or "f32" or "f64" or "f128" =>
                new TypeInfo(typeName, isReference, Protocols: BinaryFloatProtocols),

            // Decimal floating point (IEEE 754)
            "d32" or "d64" or "d128" =>
                new TypeInfo(typeName, isReference, Protocols: DecimalFloatProtocols),

            // Boolean
            "bool" => new TypeInfo(typeName, isReference, Protocols: BoolProtocols),

            // For unknown/user-defined types, return TypeInfo without protocols
            // Protocols will be resolved during semantic analysis
            _ => new TypeInfo(typeName, isReference)
        };
    }

    /// <summary>
    /// Checks if a type name is a known primitive type.
    /// </summary>
    public static bool IsPrimitive(string typeName)
    {
        return typeName switch
        {
            "s8" or "s16" or "s32" or "s64" or "s128" or "saddr" => true,
            "u8" or "u16" or "u32" or "u64" or "u128" or "uaddr" => true,
            "f16" or "f32" or "f64" or "f128" => true,
            "d32" or "d64" or "d128" => true,
            "bool" => true,
            _ => false
        };
    }
}
