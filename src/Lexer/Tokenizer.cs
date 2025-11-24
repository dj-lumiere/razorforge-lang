using Compilers.Shared.Analysis;
using Compilers.Suflae.Lexer;
using Compilers.RazorForge.Lexer;

namespace Compilers.Shared.Lexer;

public static class Tokenizer
{
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
