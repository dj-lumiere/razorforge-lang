using System.Text;

namespace Builder;

/// <summary>
/// Authoritative FNV-1a type ID computation shared across synthesis, postprocessing passes, and codegen.
/// </summary>
internal static class TypeIdHelper
{
    /// <summary>
    /// Computes the FNV-1a hash of <paramref name="fullName"/> as a type identifier.
    /// Returns 0 for <c>None</c> (the reserved absent sentinel).
    /// Returns 1 if the hash would otherwise be 0 (reserved for None).
    /// </summary>
    internal static ulong ComputeTypeId(string fullName)
    {
        if (fullName is "None" || fullName.EndsWith(value: ".None"))
        {
            return 0UL;
        }

        ulong hash = 14695981039346656037UL; // FNV-1a offset basis
        foreach (byte b in Encoding.UTF8.GetBytes(s: fullName))
        {
            hash ^= b;
            hash *= 1099511628211UL; // FNV-1a prime
        }

        return hash == 0UL
            ? 1UL
            : hash;
    }
}
