namespace SyntaxTree;

/// <summary>
/// Enumeration of unary operators that operate on a single operand.
/// Supports arithmetic, logical, bitwise, and ownership operations.
/// </summary>
/// <remarks>
/// Unary operators provide fundamental single-operand operations:
/// <list type="bullet">
/// <item>Arithmetic: -x (negation)</item>
/// <item>Logical: not condition</item>
/// <item>Bitwise: ~x (bitwise complement)</item>
/// <item>Ownership: steal x (ownership transfer)</item>
/// </list>
/// </remarks>
public enum UnaryOperator
{
    /// <summary>Arithmetic negation operator (-x)</summary>
    Minus,

    /// <summary>Logical NOT operator (not x)</summary>
    Not,

    /// <summary>Bitwise NOT/complement operator (~x)</summary>
    BitwiseNot,

    /// <summary>
    /// Ownership transfer operator (steal x).
    /// Transfers ownership from the source variable to the destination.
    /// </summary>
    /// <remarks>
    /// The steal operator performs build-time ownership tracking with runtime pass-through.
    /// After a steal, the source variable becomes a deadref and cannot be used.
    ///
    /// Stealable types:
    /// <list type="bullet">
    /// <item>Raw entities - ownership is transferred</item>
    /// </list>
    ///
    /// Non-stealable types (caught by semantic analyzer):
    /// <list type="bullet">
    /// <item>Scope-bound tokens (Viewing, Modifying, Consulting, Amending)</item>
    /// <item>Handles and links (Retained, Tracked, Guarded, Witnessed)</item>
    /// <item>Hijacked[T] - internal ownership type</item>
    /// </list>
    /// </remarks>
    Steal,

    /// <summary>
    /// Force unwrap operator (x!!).
    /// Extracts the value from a Maybe[T], panicking if None.
    /// </summary>
    /// <remarks>
    /// The force unwrap operator is a postfix unary operator that:
    /// <list type="bullet">
    /// <item>Returns the inner value if the Maybe is not None</item>
    /// <item>Panics/stops if the Maybe is None</item>
    /// </list>
    /// This should only be used when the programmer is certain the value is not None.
    /// </remarks>
    ForceUnwrap
}

/// <summary>
/// Extension memberRoutines for the <see cref="UnaryOperator"/> enum, providing string representations
/// and operator overloading support via wired memberRoutine names.
/// </summary>
internal static class UnaryOperatorExtensions
{
    extension(UnaryOperator op)
    {
        /// <summary>
        /// Returns the source-level string representation of the operator (e.g., "-", "not", "~"),
        /// or null if the operator has no simple string form.
        /// </summary>
        public string? ToStringRepresentation()
        {
            return op switch
            {
                UnaryOperator.Minus => "-",
                UnaryOperator.Not => "not",
                UnaryOperator.BitwiseNot => "~",
                UnaryOperator.Steal => "steal",
                UnaryOperator.ForceUnwrap => "!!",
                _ => null
            };
        }
        /// <summary>
        /// Gets the wired memberRoutine name for operator overloading.
        /// Returns null if the operator is not overloadable.
        /// </summary>
        public string? GetMemberRoutineName()
        {
            return op switch
            {
                UnaryOperator.Minus => "neg",
                UnaryOperator.BitwiseNot => "bitnot",
                UnaryOperator.ForceUnwrap => "unwrap",
                _ => null
            };
        }
    }
}
