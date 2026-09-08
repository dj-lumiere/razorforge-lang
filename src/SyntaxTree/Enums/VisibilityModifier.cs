/// <summary>
/// Visibility modifiers controlling WHO can access declarations.
/// Orthogonal to the routine kind (common/member/free) which controls WHERE a symbol lives.
/// </summary>
/// <remarks>
/// The visibility system is designed to be intuitive while providing precise control:
///
/// Both languages share the same visibility levels:
/// <list type="bullet">
/// <item>secret - Only the declaring module can access</item>
/// <item>posted - Open read, secret write (for member variables)</item>
/// <item>open - Any code can access</item>
/// <item>external - External/FFI linkage</item>
/// </list>
///
/// Visibility can combine with the `common` (type-level) routine kind:
/// <list type="bullet">
/// <item>open common routine Type.foo() - Anyone can call Type.foo()</item>
/// <item>secret common routine Type.bar() - Same module can call Type.bar()</item>
/// </list>
///
/// Note: 'family' (protected) was removed since inheritance is not supported.
/// </remarks>
public enum VisibilityModifier
{
    /// <summary>Only accessible within the declaring module</summary>
    Secret,

    /// <summary>Open read, secret write (open getter, secret setter)</summary>
    Posted,

    /// <summary>Accessible from anywhere</summary>
    Open,

    /// <summary>External/FFI linkage</summary>
    External
}
