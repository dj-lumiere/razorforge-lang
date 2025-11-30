using Compilers.Shared.AST;
using Compilers.Shared.Lexer;
using Compilers.Shared.Parser;

namespace Compilers.Suflae.Parser;

/// <summary>
/// Partial class containing type parsing.
/// </summary>
public partial class SuflaeParser
{
    private TypeExpression ParseType()
    {
        SourceLocation location = GetLocation();

        // Named type (identifier or type identifier)
        if (Match(TokenType.Identifier, TokenType.TypeIdentifier))
        {
            string name = PeekToken(offset: -1)
               .Text;

            // Check for generic type arguments
            if (Match(type: TokenType.Less))
            {
                var typeArgs = new List<TypeExpression>();

                do
                {
                    typeArgs.Add(item: ParseType());
                } while (Match(type: TokenType.Comma));

                Consume(type: TokenType.Greater,
                    errorMessage: "Expected '>' after type arguments");

                return new TypeExpression(Name: name,
                    GenericArguments: typeArgs,
                    Location: location);
            }

            return new TypeExpression(Name: name, GenericArguments: null, Location: location);
        }

        throw new ParseException(message: $"Expected type, got {CurrentToken.Type}");
    }
}
