using Compilers.Shared.AST;

namespace Compilers.Shared.Analysis;

/// <summary>
/// Utilities for resolving generic type templates and performing type parameter substitution.
/// This is the core of Bug 12.13 fix - enables matching concrete generic instances to templates.
/// </summary>
public static class GenericTypeResolver
{
    /// <summary>
    /// Extracts the base type name from a generic type.
    /// Examples:
    /// - "BackIndex&lt;uaddr&gt;" → "BackIndex"
    /// - "List&lt;s32&gt;" → "List"
    /// - "Dict&lt;Text, s64&gt;" → "Dict"
    /// - "s32" → "s32" (non-generic)
    /// </summary>
    public static string ExtractBaseName(string typeName)
    {
        int genericStart = typeName.IndexOf('<');
        if (genericStart < 0)
        {
            return typeName; // Not a generic type
        }

        return typeName.Substring(0, genericStart);
    }

    /// <summary>
    /// Extracts type arguments from a generic type.
    /// Examples:
    /// - "BackIndex&lt;uaddr&gt;" → ["uaddr"]
    /// - "List&lt;s32&gt;" → ["s32"]
    /// - "Dict&lt;Text, s64&gt;" → ["Text", "s64"]
    /// - "List&lt;List&lt;s32&gt;&gt;" → ["List&lt;s32&gt;"]
    /// </summary>
    public static List<string> ExtractTypeArguments(string typeName)
    {
        var args = new List<string>();

        int genericStart = typeName.IndexOf('<');
        if (genericStart < 0)
        {
            return args; // Not a generic type
        }

        // Extract everything between < and >
        int depth = 0;
        int argStart = genericStart + 1;

        for (int i = genericStart; i < typeName.Length; i++)
        {
            char c = typeName[i];

            if (c == '<')
            {
                depth++;
            }
            else if (c == '>')
            {
                depth--;
                if (depth == 0)
                {
                    // End of top-level generic arguments
                    string arg = typeName.Substring(argStart, i - argStart).Trim();
                    if (!string.IsNullOrEmpty(arg))
                    {
                        args.Add(arg);
                    }
                    break;
                }
            }
            else if (c == ',' && depth == 1)
            {
                // Comma at top level - separates arguments
                string arg = typeName.Substring(argStart, i - argStart).Trim();
                if (!string.IsNullOrEmpty(arg))
                {
                    args.Add(arg);
                }
                argStart = i + 1;
            }
        }

        return args;
    }

    /// <summary>
    /// Checks if a concrete type is an instance of a generic template.
    /// Examples:
    /// - IsInstanceOf("BackIndex&lt;uaddr&gt;", "BackIndex&lt;I&gt;") → true, {I: "uaddr"}
    /// - IsInstanceOf("List&lt;s32&gt;", "List&lt;T&gt;") → true, {T: "s32"}
    /// - IsInstanceOf("Dict&lt;Text, s64&gt;", "Dict&lt;K, V&gt;") → true, {K: "Text", V: "s64"}
    /// - IsInstanceOf("s32", "List&lt;T&gt;") → false
    /// </summary>
    public static bool IsInstanceOf(
        string concreteType,
        string templateType,
        out Dictionary<string, string> typeParameterMap)
    {
        typeParameterMap = new Dictionary<string, string>();

        // Extract base names
        string concreteBase = ExtractBaseName(concreteType);
        string templateBase = ExtractBaseName(templateType);

        // Base names must match
        if (concreteBase != templateBase)
        {
            return false;
        }

        // Extract type arguments
        var concreteArgs = ExtractTypeArguments(concreteType);
        var templateArgs = ExtractTypeArguments(templateType);

        // Argument counts must match
        if (concreteArgs.Count != templateArgs.Count)
        {
            return false;
        }

        // Map each template parameter to its concrete type
        for (int i = 0; i < templateArgs.Count; i++)
        {
            string templateParam = templateArgs[i];
            string concreteArg = concreteArgs[i];

            // Check if template parameter is already mapped
            if (typeParameterMap.TryGetValue(templateParam, out string? existingMapping))
            {
                // Verify consistency - same parameter must map to same type
                if (existingMapping != concreteArg)
                {
                    return false;
                }
            }
            else
            {
                typeParameterMap[templateParam] = concreteArg;
            }
        }

        return true;
    }

    /// <summary>
    /// Substitutes type parameters in a type name using the provided mapping.
    /// Examples:
    /// - SubstituteType("I", {I: "uaddr"}) → "uaddr"
    /// - SubstituteType("List&lt;T&gt;", {T: "s32"}) → "List&lt;s32&gt;"
    /// - SubstituteType("Dict&lt;K, V&gt;", {K: "Text", V: "s64"}) → "Dict&lt;Text, s64&gt;"
    /// </summary>
    public static string SubstituteType(string typeName, Dictionary<string, string> typeParameterMap)
    {
        // If the entire type name is a type parameter, replace it
        if (typeParameterMap.TryGetValue(typeName, out string? replacement))
        {
            return replacement;
        }

        // Check if this is a generic type that needs substitution
        int genericStart = typeName.IndexOf('<');
        if (genericStart < 0)
        {
            return typeName; // No generic parameters
        }

        // Extract and substitute type arguments
        string baseName = typeName.Substring(0, genericStart);
        var args = ExtractTypeArguments(typeName);

        var substitutedArgs = new List<string>();
        foreach (string arg in args)
        {
            // Recursively substitute in case of nested generics
            string substituted = SubstituteType(arg, typeParameterMap);
            substitutedArgs.Add(substituted);
        }

        // Reconstruct the type with substituted arguments
        return $"{baseName}<{string.Join(", ", substitutedArgs)}>";
    }

    /// <summary>
    /// Substitutes type parameters in a TypeInfo object.
    /// Creates a new TypeInfo with all type parameters replaced.
    /// </summary>
    public static TypeInfo SubstituteTypeInfo(TypeInfo typeInfo, Dictionary<string, string> typeParameterMap)
    {
        if (typeInfo == null)
        {
            return new TypeInfo(Name: "unknown", IsReference: false);
        }

        string substitutedName = SubstituteType(typeInfo.Name, typeParameterMap);

        // Handle generic arguments recursively
        List<TypeInfo>? substitutedGenericArgs = null;
        if (typeInfo.GenericArguments != null && typeInfo.GenericArguments.Count > 0)
        {
            substitutedGenericArgs = new List<TypeInfo>();
            foreach (var arg in typeInfo.GenericArguments)
            {
                substitutedGenericArgs.Add(SubstituteTypeInfo(arg, typeParameterMap));
            }
        }

        return new TypeInfo(
            Name: substitutedName,
            IsReference: typeInfo.IsReference,
            GenericArguments: substitutedGenericArgs,
            IsGenericParameter: typeInfo.IsGenericParameter,
            Protocols: typeInfo.Protocols
        );
    }

    /// <summary>
    /// Generates all possible template keys for a method on a generic type.
    /// Used to search for template definitions in the symbol table.
    ///
    /// Examples:
    /// - For type "BackIndex&lt;uaddr&gt;" with method "resolve":
    ///   Returns: ["BackIndex&lt;uaddr&gt;.resolve", "BackIndex&lt;I&gt;.resolve", "BackIndex.resolve"]
    /// </summary>
    public static List<string> GenerateTemplateCandidates(string typeName, string methodName)
    {
        var candidates = new List<string>();

        // First, try exact match
        candidates.Add($"{typeName}.{methodName}");

        // If generic, try with template parameters
        string baseName = ExtractBaseName(typeName);
        var typeArgs = ExtractTypeArguments(typeName);

        if (typeArgs.Count > 0)
        {
            // Generate template with single-letter parameters: T, U, V, I, K, etc.
            var commonParams = new[] { "T", "U", "V", "I", "K", "E", "R", "A", "B", "C" };

            for (int paramCount = typeArgs.Count; paramCount <= typeArgs.Count; paramCount++)
            {
                var paramNames = commonParams.Take(paramCount).ToList();
                string templateType = $"{baseName}<{string.Join(", ", paramNames)}>";
                candidates.Add($"{templateType}.{methodName}");
            }

            // Also try without type parameters
            candidates.Add($"{baseName}.{methodName}");
        }

        return candidates.Distinct().ToList();
    }
}