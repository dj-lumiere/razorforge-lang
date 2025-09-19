using Compilers.Shared.Analysis;
using Compilers.Cake.Lexer;
using Compilers.RazorForge.Lexer;

namespace Compilers.Shared.Lexer;

public static class Tokenizer
{
    public static List<Token> Tokenize(string source, Language language)
    {
        BaseTokenizer tokenizer = language switch
        {
            Language.Cake => new CakeTokenizer(source),
            Language.RazorForge => new RazorForgeTokenizer(source),
            _ => throw new ArgumentException($"Unsupported language: {language}")
        };

        return tokenizer.Tokenize();
    }

    public static bool IsScriptMode(string source, Language language)
    {
        if (language != Language.Cake)
            return false;

        var cakeTokenizer = new CakeTokenizer(source);
        cakeTokenizer.Tokenize(); // Need to tokenize to detect definitions
        return cakeTokenizer.IsScriptMode;
    }

}