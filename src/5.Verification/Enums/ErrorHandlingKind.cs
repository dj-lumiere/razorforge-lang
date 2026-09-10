namespace Builder.Verification.Enums;

/// <summary>
/// Kind of error handling type.
/// </summary>
public enum ErrorHandlingKind
{
    /// <summary>Maybe&lt;T&gt; / T? - optional value (None or value).</summary>
    Maybe,

    /// <summary>Result&lt;T&gt; - error or value.</summary>
    Result,

    /// <summary>Lookup&lt;T&gt; - three-way: error, absent, or value.</summary>
    Lookup
}
