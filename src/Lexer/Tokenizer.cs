using Compilers.Shared.Analysis;
using Compilers.Suflae.Lexer;
using Compilers.RazorForge.Lexer;

namespace Compilers.Shared.Lexer;

public static class Tokenizer
{
    /// <summary>
    /// Tokenizes the given source code based on the specified programming language.
    /// </summary>
    /// <param name="source">The source code to be tokenized.</param>
    /// <param name="language">The programming language of the source code.</param>
    /// <returns>A list of tokens representing the lexical elements of the source code.</returns>
    /// <exception cref="ArgumentException">Thrown when the specified language is not supported.</exception>
    public static List<Token> Tokenize(string source, Language language)
    {
        BaseTokenizer tokenizer = language switch
        {
            Language.Suflae => new SuflaeTokenizer(source: source),
            Language.RazorForge => new RazorForgeTokenizer(source: source),
            _ => throw new ArgumentException(message: $"Unsupported language: {language}")
        };

        return tokenizer.Tokenize();
    }

    /// <summary>
    /// Determines whether the given source code is in script mode for the specified programming language.
    /// </summary>
    /// <param name="source">The source code to evaluate.</param>
    /// <param name="language">The programming language of the source code.</param>
    /// <returns>True if the source code is in script mode; otherwise, false.</returns>
    public static bool IsScriptMode(string source, Language language)
    {
        if (language != Language.Suflae)
        {
            return false;
        }

        var suflaeTokenizer = new SuflaeTokenizer(source: source);
        suflaeTokenizer.Tokenize(); // Need to tokenize to detect definitions
        return suflaeTokenizer.IsScriptMode;
    }
}
