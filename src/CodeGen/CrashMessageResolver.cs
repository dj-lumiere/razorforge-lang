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
        if (_initialized)
        {
            return;
        }

        _initialized = true;

        string errorsPath = Path.Combine(path1: _stdlibPath, path2: "errors");
        if (!Directory.Exists(path: errorsPath))
        {
            return;
        }

        foreach (string file in Directory.GetFiles(path: errorsPath, searchPattern: "*.rf"))
        {
            try
            {
                ParseErrorFile(filePath: file);
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
        return _staticMessages.TryGetValue(key: errorTypeName, value: out string? message)
            ? message
            : null;
    }

    /// <summary>
    /// Checks if an error type has a dynamic message (needs runtime resolution).
    /// </summary>
    public bool HasDynamicMessage(string errorTypeName)
    {
        Initialize();
        return _dynamicMessageTypes.Contains(item: errorTypeName);
    }

    /// <summary>
    /// Checks if an error type is known to the resolver.
    /// </summary>
    public bool IsKnownErrorType(string errorTypeName)
    {
        Initialize();
        return _staticMessages.ContainsKey(key: errorTypeName) ||
               _dynamicMessageTypes.Contains(item: errorTypeName);
    }

    private void ParseErrorFile(string filePath)
    {
        string content = File.ReadAllText(path: filePath);

        // Find record definitions that follow Crashable
        // Match: record TypeName follows Crashable {
        var recordStartPattern = new Regex(pattern: @"record\s+(\w+)\s+follows\s+Crashable\s*\{");

        foreach (Match recordMatch in recordStartPattern.Matches(input: content))
        {
            string typeName = recordMatch.Groups[groupnum: 1].Value;
            int braceStart = recordMatch.Index + recordMatch.Length - 1;

            // Find matching closing brace for the record
            string recordBody = ExtractBracedContent(content: content, braceStart: braceStart);
            if (recordBody == null)
            {
                continue;
            }

            // Find crash_message routine - allow for optional 'me' parameter
            // Patterns: crash_message() or crash_message(me) or crash_message(me, ...)
            var crashMsgPattern = new Regex(
                pattern: @"routine\s+crash_message\s*\([^)]*\)\s*->\s*Text<\w+>\s*\{",
                options: RegexOptions.Singleline);

            Match crashMsgMatch = crashMsgPattern.Match(input: recordBody);
            if (crashMsgMatch.Success)
            {
                int methodBraceStart = crashMsgMatch.Index + crashMsgMatch.Length - 1;
                string methodBody =
                    ExtractBracedContent(content: recordBody, braceStart: methodBraceStart);
                if (methodBody == null)
                {
                    continue;
                }

                methodBody = methodBody.Trim();

                // Check if it's a simple return of a string literal
                var simpleReturnPattern = new Regex(pattern: @"^\s*return\s+""([^""]+)""\s*$");
                Match simpleMatch = simpleReturnPattern.Match(input: methodBody);

                if (simpleMatch.Success)
                {
                    // Static message - just a string literal
                    _staticMessages[key: typeName] = simpleMatch.Groups[groupnum: 1].Value;
                }
                else if (methodBody.Contains(value: "me.") || methodBody.Contains(value: "f\"") ||
                         methodBody.Contains(value: "{"))
                {
                    // Dynamic message - uses fields or interpolation
                    _dynamicMessageTypes.Add(item: typeName);
                }
                else
                {
                    // Unknown pattern - treat as dynamic
                    _dynamicMessageTypes.Add(item: typeName);
                }
            }
        }
    }

    /// <summary>
    /// Extracts content between matching braces starting at the given position.
    /// </summary>
    private static string? ExtractBracedContent(string content, int braceStart)
    {
        if (braceStart >= content.Length || content[index: braceStart] != '{')
        {
            return null;
        }

        int depth = 1;
        int i = braceStart + 1;

        while (i < content.Length && depth > 0)
        {
            char c = content[index: i];
            if (c == '{')
            {
                depth++;
            }
            else if (c == '}')
            {
                depth--;
            }

            i++;
        }

        if (depth != 0)
        {
            return null;
        }

        // Return content between braces (excluding the braces themselves)
        return content.Substring(startIndex: braceStart + 1, length: i - braceStart - 2);
    }
}
