namespace TypeModel.Symbols;

/// <summary>
/// Parses a stored <c>@link(...)</c> attribute string into its structured parts. The attribute
/// associates a <c>C::</c> extern with a foreign library (whose static/dynamic linkage + calling
/// convention are declared in <c>config.toml</c>, NOT here) and optionally overrides the linked
/// symbol name.
/// <para>Accepted forms (all lowered to the same result):</para>
/// <list type="bullet">
///   <item><c>@link("SDL2")</c> — legacy positional, library only.</item>
///   <item><c>@link(SDL2)</c> — bare-identifier library reference.</item>
///   <item><c>@link(lib: "SDL2")</c> — named library.</item>
///   <item><c>@link(lib: "SDL2", symbol: "SDL_Init")</c> — library + symbol-name override.</item>
/// </list>
/// The annotation is stored by the parser with named args rendered as <c>key=value</c> and string
/// values keeping their quotes (e.g. <c>link(lib="SDL2", symbol="SDL_Init")</c>).
/// </summary>
public static class LinkAnnotation
{
    /// <summary>
    /// Parses one annotation string. Returns <c>(null, null)</c> if it is not a <c>link(...)</c>
    /// attribute. <c>Library</c> is the referenced library name; <c>Symbol</c> is the explicit exported
    /// symbol to link against, or null to link by the bare routine name.
    /// </summary>
    public static (string? Library, string? Symbol) Parse(string annotation)
    {
        string a = annotation.Trim();
        if (!a.StartsWith(value: "link(") || !a.EndsWith(value: ")"))
        {
            return (null, null);
        }

        string inner = a["link(".Length..^1];
        string? lib = null;
        string? symbol = null;
        int positional = 0;

        // Library names and symbols never contain commas, so a plain split is sufficient.
        foreach (string rawPart in inner.Split(separator: ','))
        {
            string part = rawPart.Trim();
            if (part.Length == 0)
            {
                continue;
            }

            int eq = part.IndexOf(value: '=');
            if (eq >= 0)
            {
                string key = part[..eq].Trim();
                string val = Unquote(s: part[(eq + 1)..]);
                switch (key)
                {
                    case "lib" or "library": lib = val; break;
                    case "symbol" or "entry": symbol = val; break;
                }
            }
            else
            {
                // Positional: first argument is the library name.
                if (positional == 0)
                {
                    lib = Unquote(s: part);
                }

                positional++;
            }
        }

        return (lib, symbol);
    }

    private static string Unquote(string s) => s.Trim().Trim('"');
}
