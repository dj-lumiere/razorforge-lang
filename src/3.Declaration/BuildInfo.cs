using System.Reflection;

namespace Compiler.Declaration;

/// <summary>
/// Build metadata surfaced from the csproj PropertyGroup via <c>&lt;AssemblyMetadata&gt;</c> — the single
/// source of truth for the two language version lines. Bump <c>&lt;RazorForgeVersion&gt;</c> /
/// <c>&lt;SuflaeVersion&gt;</c> in the csproj, never a literal in code.
/// </summary>
/// <remarks>
/// Read by the CLI version banner (<c>Builder.Program</c>) and by the compile-time-folded BuilderQuery
/// <c>builder_version()</c> intrinsic (<c>WiredRoutinePass</c>), which selects the line by the compiled
/// language. Both live in this assembly, so <c>typeof(BuildInfo).Assembly</c> carries the attributes.
/// </remarks>
public static class BuildInfo
{
    /// <summary>Reads an <c>&lt;AssemblyMetadata&gt;</c> value by key, or <c>null</c> if absent.</summary>
    public static string? AssemblyMetadata(string key)
    {
        return typeof(BuildInfo).Assembly
            .GetCustomAttributes(attributeType: typeof(AssemblyMetadataAttribute), inherit: false)
            .OfType<AssemblyMetadataAttribute>()
            .FirstOrDefault(predicate: a => a.Key == key)
           ?.Value;
    }

    /// <summary>The RazorForge version line (<c>&lt;RazorForgeVersion&gt;</c>); <c>"0.0.0"</c> fallback.</summary>
    public static string RazorForgeVersion => AssemblyMetadata(key: "RazorForgeVersion") ?? "0.0.0";

    /// <summary>The Suflae version line (<c>&lt;SuflaeVersion&gt;</c>); <c>"0.0.0"</c> fallback.</summary>
    public static string SuflaeVersion => AssemblyMetadata(key: "SuflaeVersion") ?? "0.0.0";
}
