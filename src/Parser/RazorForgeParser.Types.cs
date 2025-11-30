using Compilers.Shared.AST;
using Compilers.Shared.Lexer;
using Compilers.Shared.Parser;

namespace Compilers.RazorForge.Parser;

/// <summary>
/// Partial class containing type parsing, generic constraints, and type utilities.
/// </summary>
public partial class RazorForgeParser
{
    private TypeExpression ParseType()
    {
        SourceLocation location = GetLocation();

        // Basic types - use identifiers for type names
        // RazorForge doesn't have specific type tokens, just identifier tokens for types

        // Tuple type: (T) or (A, B, C)
        // Used in lambda/routine types like Routine<(T), R>
        if (Match(type: TokenType.LeftParen))
        {
            var tupleElements = new List<TypeExpression>();

            if (!Check(type: TokenType.RightParen))
            {
                do
                {
                    tupleElements.Add(item: ParseType());
                } while (Match(type: TokenType.Comma));
            }

            Consume(type: TokenType.RightParen,
                errorMessage: "Expected ')' after tuple type elements");

            // Represent tuple as a special type with name "__Tuple" and the element types as generics
            return new TypeExpression(Name: "__Tuple",
                GenericArguments: tupleElements,
                Location: location);
        }

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
                    typeArgs.Add(item: ParseTypeOrConstGeneric());
                } while (Match(type: TokenType.Comma));

                ConsumeGreaterForGeneric(errorMessage: "Expected '>' after type arguments");

                return new TypeExpression(Name: name,
                    GenericArguments: typeArgs,
                    Location: location);
            }

            return new TypeExpression(Name: name, GenericArguments: null, Location: location);
        }

        throw new ParseException(
            message: $"Expected type, got {CurrentToken.Type} ('{CurrentToken.Text}')");
    }

    /// <summary>
    /// Parses a type expression or a const generic (numeric literal).
    /// Used for generic arguments like ValueText&lt;letter32, 256&gt;.
    /// </summary>
    private TypeExpression ParseTypeOrConstGeneric()
    {
        SourceLocation location = GetLocation();

        // Check for numeric literal (const generic)
        if (Match(TokenType.S64Literal,
                TokenType.U64Literal,
                TokenType.S32Literal,
                TokenType.U32Literal,
                TokenType.S16Literal,
                TokenType.U16Literal,
                TokenType.S8Literal,
                TokenType.U8Literal))
        {
            string value = PeekToken(offset: -1)
               .Text;
            // Represent const generic as a special type expression with the literal value as name
            return new TypeExpression(Name: value, GenericArguments: null, Location: location);
        }

        // Otherwise parse as normal type
        return ParseType();
    }

    /// <summary>
    /// Consumes a '>' token for closing generic type arguments.
    /// Handles the case where '>>' was tokenized as RightShift by splitting it
    /// and leaving one '>' for the next parse.
    /// </summary>
    private void ConsumeGreaterForGeneric(string errorMessage)
    {
        if (Match(type: TokenType.Greater))
        {
            // Simple case - just a single '>'
            return;
        }

        if (Check(type: TokenType.RightShift))
        {
            // '>>' was tokenized as RightShift - we need to split it
            // Replace the current RightShift token with a single Greater token
            // and leave a Greater for the next parse
            Token currentToken = CurrentToken;
            var newGreater = new Token(Type: TokenType.Greater,
                Text: ">",
                Line: currentToken.Line,
                Column: currentToken.Column + 1); // Second > is one position after

            // Advance past the RightShift
            Advance();

            // Insert a Greater token to be consumed next
            // We do this by adjusting the position and inserting
            InsertToken(token: newGreater);
            return;
        }

        // Neither > nor >> found - error
        throw new ParseException(
            message: $"{errorMessage}. Expected Greater, got {CurrentToken.Type}.");
    }

    /// <summary>
    /// Inserts a token at the current position to be parsed next.
    /// Used for splitting '>>' into two '>' tokens.
    /// </summary>
    private void InsertToken(Token token)
    {
        Tokens.Insert(index: Position, item: token);
    }

    /// <summary>
    /// Parses generic constraints for type parameters.
    /// Supports inline constraints (T follows Protocol) and where clauses.
    /// </summary>
    private List<GenericConstraintDeclaration>? ParseGenericConstraints(
        List<string>? genericParams)
    {
        if (genericParams == null || genericParams.Count == 0)
        {
            return null;
        }

        var constraints = new List<GenericConstraintDeclaration>();

        // Check for where clause: where T follows Protocol, U from BaseType
        if (Match(type: TokenType.Where))
        {
            do
            {
                SourceLocation location = GetLocation();
                string paramName = ConsumeIdentifier(errorMessage: "Expected type parameter name");

                // Verify this parameter was declared
                if (!genericParams.Contains(item: paramName))
                {
                    throw new ParseException(
                        message:
                        $"Type parameter '{paramName}' not declared in generic parameters");
                }

                // Parse constraint kind and types
                if (Match(type: TokenType.Follows))
                {
                    // T follows Protocol1, Protocol2
                    var constraintTypes = new List<TypeExpression>();
                    do
                    {
                        constraintTypes.Add(item: ParseType());
                    } while (Match(type: TokenType.Comma) && !Check(type: TokenType.Identifier));

                    constraints.Add(item: new GenericConstraintDeclaration(
                        ParameterName: paramName,
                        ConstraintType: ConstraintKind.Follows,
                        ConstraintTypes: constraintTypes,
                        Location: location));
                }
                else if (Match(type: TokenType.From))
                {
                    // T from BaseType
                    TypeExpression baseType = ParseType();
                    constraints.Add(item: new GenericConstraintDeclaration(
                        ParameterName: paramName,
                        ConstraintType: ConstraintKind.From,
                        ConstraintTypes: new List<TypeExpression> { baseType },
                        Location: location));
                }
                else if (Check(type: TokenType.Colon))
                {
                    // T: record or T: entity
                    Advance(); // consume ':'
                    if (Match(type: TokenType.Record))
                    {
                        constraints.Add(item: new GenericConstraintDeclaration(
                            ParameterName: paramName,
                            ConstraintType: ConstraintKind.ValueType,
                            ConstraintTypes: null,
                            Location: location));
                    }
                    else if (Match(type: TokenType.Entity))
                    {
                        constraints.Add(item: new GenericConstraintDeclaration(
                            ParameterName: paramName,
                            ConstraintType: ConstraintKind.ReferenceType,
                            ConstraintTypes: null,
                            Location: location));
                    }
                    else
                    {
                        throw new ParseException(
                            message: "Expected 'record' or 'entity' after ':' in constraint");
                    }
                }
                else
                {
                    throw new ParseException(
                        message: "Expected 'follows', 'from', or ':' in generic constraint");
                }

                // Continue parsing if there's a comma
            } while (Match(type: TokenType.Comma));
        }

        return constraints.Count > 0
            ? constraints
            : null;
    }

    /// <summary>
    /// Checks if a name is a primitive type name (used for generic argument disambiguation).
    /// </summary>
    private static bool IsPrimitiveTypeName(string name)
    {
        return name switch
        {
            // Signed integers
            "s8" or "s16" or "s32" or "s64" or "s128" or "saddr" => true,
            // Unsigned integers
            "u8" or "u16" or "u32" or "u64" or "u128" or "uaddr" => true,
            // Floating point (IEEE754 binary)
            "f16" or "f32" or "f64" or "f128" => true,
            // Floating point (IEEE754 decimal)
            "d32" or "d64" or "d128" => true,
            // Boolean
            "bool" => true,
            // Character types
            "letter" or "letter8" or "letter16" => true,
            // Text types
            "text" or "text8" or "text16" => true,
            // Void type
            "void" => true,
            _ => false
        };
    }
}
