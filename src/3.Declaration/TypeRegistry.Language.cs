using TypeModel.Enums;
using TypeModel.Types;

namespace Builder.Declaration;

public sealed partial class TypeRegistry
{
    #region Language-Specific Validation

    /// <summary>
    /// Validates that a type is allowed for the current language.
    /// </summary>
    /// <param name="type">The type to validate.</param>
    /// <returns>True if the type is allowed for the current language, false otherwise.</returns>
    public bool IsTypeAllowedForLanguage(TypeSymbol type)
    {
        // Memory wrapper types are RazorForge only
        if (IsMemoryWrapperType(typeName: type.Name) && Language == Language.Suflae)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Checks if a type name is a RazorForge-specific memory wrapper type.
    /// </summary>
    /// <param name="typeName">The name of the type to check.</param>
    /// <returns>True if the type is a memory wrapper, false otherwise.</returns>
    private static bool IsMemoryWrapperType(string typeName)
    {
        return typeName is RuntimeContract.Viewing or RuntimeContract.Modifying
            or RuntimeContract.Consulting or RuntimeContract.Amending or RuntimeContract.Hijacked
            or RuntimeContract.Guarded or RuntimeContract.Witnessed or RuntimeContract.Retained
            or RuntimeContract.Tracked;
    }

    #endregion
}
