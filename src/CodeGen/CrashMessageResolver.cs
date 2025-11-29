using System.Text.RegularExpressions;

namespace Compilers.Shared.CodeGen;

/// <summary>
/// Resolves crash messages from error type definitions in stdlib.
/// Extracts static crash_message() return values from .rf source files.
/// This ensures error messages are defined in RazorForge, not hardcoded in the compiler.
/// </summary>
public class CrashMessageResolver
{
    private readonly string _stdlibPath;
    private readonly Dictionary<string, string> _staticMessages = new();
    private readonly HashSet<string> _dynamicMessageTypes = new();
    private bool _initialized;

    public CrashMessageResolver(string stdlibPath)
    {
        _stdlibPath = stdlibPath;
    }

    /// <summary>
    /// Initializes the resolver by scanning error type definitions.
    /// </summary>
    public void Initialize()
    {
        if (_initialized) return;
        _initialized = true;

        string errorsPath = Path.Combine(_stdlibPath, "errors");
        if (!Directory.Exists(errorsPath))
        {
            return;
        }

        foreach (string file in Directory.GetFiles(errorsPath, "*.rf"))
        {
            try
            {
                ParseErrorFile(file);
            }
            catch
            {
                // Ignore parse errors - fall back to runtime
            }
        }
    }

    /// <summary>
    /// Gets the static crash message for an error type, if available.
    /// Returns null if the message is dynamic (uses fields/interpolation).
    /// </summary>
    public string? GetStaticMessage(string errorTypeName)
    {
        Initialize();
        return _staticMessages.TryGetValue(errorTypeName, out var message) ? message : null;
    }

    /// <summary>
    /// Checks if an error type has a dynamic message (needs runtime resolution).
    /// </summary>
    public bool HasDynamicMessage(string errorTypeName)
    {
        Initialize();
        return _dynamicMessageTypes.Contains(errorTypeName);
    }

    /// <summary>
    /// Checks if an error type is known to the resolver.
    /// </summary>
    public bool IsKnownErrorType(string errorTypeName)
    {
        Initialize();
        return _staticMessages.ContainsKey(errorTypeName) || _dynamicMessageTypes.Contains(errorTypeName);
    }

    private void ParseErrorFile(string filePath)
    {
        string content = File.ReadAllText(filePath);

        // Find record definitions that follow Crashable
        // Match: record TypeName follows Crashable {
        var recordStartPattern = new Regex(@"record\s+(\w+)\s+follows\s+Crashable\s*\{");

        foreach (Match recordMatch in recordStartPattern.Matches(content))
        {
            string typeName = recordMatch.Groups[1].Value;
            int braceStart = recordMatch.Index + recordMatch.Length - 1;

            // Find matching closing brace for the record
            string recordBody = ExtractBracedContent(content, braceStart);
            if (recordBody == null) continue;

            // Find crash_message routine - allow for optional 'me' parameter
            // Patterns: crash_message() or crash_message(me) or crash_message(me, ...)
            var crashMsgPattern = new Regex(
                @"routine\s+crash_message\s*\([^)]*\)\s*->\s*Text<\w+>\s*\{",
                RegexOptions.Singleline);

            var crashMsgMatch = crashMsgPattern.Match(recordBody);
            if (crashMsgMatch.Success)
            {
                int methodBraceStart = crashMsgMatch.Index + crashMsgMatch.Length - 1;
                string methodBody = ExtractBracedContent(recordBody, methodBraceStart);
                if (methodBody == null) continue;

                methodBody = methodBody.Trim();

                // Check if it's a simple return of a string literal
                var simpleReturnPattern = new Regex(@"^\s*return\s+""([^""]+)""\s*$");
                var simpleMatch = simpleReturnPattern.Match(methodBody);

                if (simpleMatch.Success)
                {
                    // Static message - just a string literal
                    _staticMessages[typeName] = simpleMatch.Groups[1].Value;
                }
                else if (methodBody.Contains("me.") || methodBody.Contains("f\"") || methodBody.Contains("{"))
                {
                    // Dynamic message - uses fields or interpolation
                    _dynamicMessageTypes.Add(typeName);
                }
                else
                {
                    // Unknown pattern - treat as dynamic
                    _dynamicMessageTypes.Add(typeName);
                }
            }
        }
    }

    /// <summary>
    /// Extracts content between matching braces starting at the given position.
    /// </summary>
    private static string? ExtractBracedContent(string content, int braceStart)
    {
        if (braceStart >= content.Length || content[braceStart] != '{')
            return null;

        int depth = 1;
        int i = braceStart + 1;

        while (i < content.Length && depth > 0)
        {
            char c = content[i];
            if (c == '{') depth++;
            else if (c == '}') depth--;
            i++;
        }

        if (depth != 0) return null;

        // Return content between braces (excluding the braces themselves)
        return content.Substring(braceStart + 1, i - braceStart - 2);
    }
}
