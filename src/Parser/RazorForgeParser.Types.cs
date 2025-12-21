using Compilers.Shared.AST;
using Compilers.Shared.Lexer;
using Compilers.Shared.Parser;

namespace Compilers.RazorForge.Parser;

/// <summary>
/// Partial class containing type parsing, generic constraints, and type utilities.
/// </summary>
public partial class RazorForgeParser
{
    /// <summary>
    /// Parses a type expression.
    /// Supports: named types, generic types (Type&lt;T&gt;), Routine types (Routine&lt;A, B, R&gt;),
    /// and Me (self type).
    /// </summary>
    /// <returns>A <see cref="TypeExpression"/> AST node.</returns>
    private TypeExpression ParseType()
    {
        SourceLocation location = GetLocation();

        // Me - self type in protocols/methods (like Self in Rust)
        if (Match(type: TokenType.MyType))
        {
            return new TypeExpression(Name: "Me", GenericArguments: null, Location: location);
        }

        // Routine type: Routine<P1, P2, ..., R> - arity-based function types
        // The 'Routine' keyword is also a valid type name for function types
        if (Match(type: TokenType.Routine))
        {
            string name = "Routine";

            // Bare 'Routine' without generics - represents a zero-arg void routine
            if (!Match(type: TokenType.Less))
            {
                return new TypeExpression(Name: name, GenericArguments: null, Location: location);
            }

            // Routine types require generic arguments: Routine<R>, Routine<P, R>, Routine<P1, P2, R>
            var typeArgs = new List<TypeExpression>();

            do
            {
                typeArgs.Add(item: ParseType());
            } while (Match(type: TokenType.Comma));

            ConsumeGreaterForGeneric(errorMessage: "Expected '>' after Routine type arguments");

            return new TypeExpression(Name: name, GenericArguments: typeArgs, Location: location);
        }

        // Named type (identifier or type identifier)
        if (Match(TokenType.Identifier, TokenType.TypeIdentifier))
        {
            string name = PeekToken(offset: -1)
               .Text;

            // Check for generic type arguments
            if (!Match(type: TokenType.Less))
            {
                return new TypeExpression(Name: name, GenericArguments: null, Location: location);
            }

            var typeArgs = new List<TypeExpression>();

            do
            {
                typeArgs.Add(item: ParseTypeOrConstGeneric());
            } while (Match(type: TokenType.Comma));

            ConsumeGreaterForGeneric(errorMessage: "Expected '>' after type arguments");

            return new TypeExpression(Name: name, GenericArguments: typeArgs, Location: location);
        }

        throw new ParseException(message: $"Expected type, got {CurrentToken.Type} ('{CurrentToken.Text}')");
    }

    /// <summary>
    /// Parses a type expression or a const generic literal.
    /// Used for generic arguments like ValueText&lt;letter32, 256&gt;.
    /// Supports: integers, booleans, letters, and choice values (e.g., Color.Red).
    /// </summary>
    private TypeExpression ParseTypeOrConstGeneric()
    {
        SourceLocation location = GetLocation();

        // Check for boolean literal (const generic)
        if (Match(TokenType.True, TokenType.False))
        {
            string value = PeekToken(offset: -1)
               .Text;
            return new TypeExpression(Name: value, GenericArguments: null, Location: location);
        }

        // Check for integer literal (const generic)
        // Support both typed literals (10u32) and untyped literals (10)
        if (Match(TokenType.Integer,
                TokenType.S64Literal,
                TokenType.U64Literal,
                TokenType.S32Literal,
                TokenType.U32Literal,
                TokenType.S16Literal,
                TokenType.U16Literal,
                TokenType.S8Literal,
                TokenType.U8Literal,
                TokenType.S128Literal,
                TokenType.U128Literal,
                TokenType.SaddrLiteral,
                TokenType.UaddrLiteral))
        {
            string value = PeekToken(offset: -1)
               .Text;
            return new TypeExpression(Name: value, GenericArguments: null, Location: location);
        }

        // Check for letter/character literal (const generic)
        if (Match(TokenType.LetterLiteral, TokenType.ByteLiteral))
        {
            string value = PeekToken(offset: -1)
               .Text;
            return new TypeExpression(Name: value, GenericArguments: null, Location: location);
        }

        // Check for choice value (const generic): ChoiceType.CASE_NAME
        // TypeIdentifier starts with uppercase (choice types follow type naming conventions)
        // Semantic analysis will validate that the type is actually a choice type
        if (Check(type: TokenType.TypeIdentifier) && PeekToken(offset: 1)
               .Type == TokenType.Dot)
        {
            // Parse choice value: Type.CASE
            string typeName = Advance()
               .Text;
            Advance(); // consume dot
            string caseName = ConsumeIdentifier(errorMessage: "Expected choice case name after '.'");
            string value = $"{typeName}.{caseName}";
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
        throw new ParseException(message: $"{errorMessage}. Expected Greater, got {CurrentToken.Type}.");
    }

    /// <summary>
    /// Inserts a token at the current position to be parsed next.
    /// Used for splitting '>>' into two '>' tokens.
    /// </summary>
    private void InsertToken(Token token)
    {
        tokens.Insert(index: Position, item: token);
    }

    /// <summary>
    /// Parses generic parameters with optional inline constraints like &lt;T follows Integral&gt;.
    /// Returns both the parameter names and any inline constraints found.
    /// </summary>
    private (List<string> genericParams, List<GenericConstraintDeclaration>? inlineConstraints) ParseGenericParametersWithConstraints()
    {
        var genericParams = new List<string>();
        var inlineConstraints = new List<GenericConstraintDeclaration>();

        do
        {
            SourceLocation location = GetLocation();
            string paramName = ConsumeIdentifier(errorMessage: "Expected generic parameter name");
            genericParams.Add(item: paramName);

            // Check for inline constraint: T follows Protocol or T is record/entity/resident
            if (Match(type: TokenType.Follows))
            {
                // Parse protocol constraints: T follows Protocol
                var constraintTypes = new List<TypeExpression>();
                do
                {
                    constraintTypes.Add(item: ParseType());
                    // Continue if comma but next token is NOT an identifier followed by follows/is/in or greater
                    // This handles both "T follows A, B" (multiple protocols) and "T follows A, U follows B" (next param)
                } while (Match(type: TokenType.Comma) && !Check(type: TokenType.Greater) && !(Check(type: TokenType.Identifier) && (PeekToken(offset: 1)
                            .Type == TokenType.Follows || PeekToken(offset: 1)
                            .Type == TokenType.Is || PeekToken(offset: 1)
                            .Type == TokenType.In)));

                inlineConstraints.Add(item: new GenericConstraintDeclaration(ParameterName: paramName,
                    ConstraintType: ConstraintKind.Follows,
                    ConstraintTypes: constraintTypes,
                    Location: location));
            }
            else if (Match(type: TokenType.Is))
            {
                // Parse type kind constraints: T is record/entity/resident/routine/choice/variant/mutant
                // Or const generic type constraints: N is uaddr/s32/etc.
                if (Match(type: TokenType.Record))
                {
                    inlineConstraints.Add(item: new GenericConstraintDeclaration(ParameterName: paramName,
                        ConstraintType: ConstraintKind.ValueType,
                        ConstraintTypes: null,
                        Location: location));
                }
                else if (Match(type: TokenType.Entity))
                {
                    inlineConstraints.Add(item: new GenericConstraintDeclaration(ParameterName: paramName,
                        ConstraintType: ConstraintKind.ReferenceType,
                        ConstraintTypes: null,
                        Location: location));
                }
                else if (Match(type: TokenType.Resident))
                {
                    inlineConstraints.Add(item: new GenericConstraintDeclaration(ParameterName: paramName,
                        ConstraintType: ConstraintKind.ResidentType,
                        ConstraintTypes: null,
                        Location: location));
                }
                else if (Match(type: TokenType.Routine))
                {
                    inlineConstraints.Add(item: new GenericConstraintDeclaration(ParameterName: paramName,
                        ConstraintType: ConstraintKind.RoutineType,
                        ConstraintTypes: null,
                        Location: location));
                }
                else if (Match(type: TokenType.Choice))
                {
                    inlineConstraints.Add(item: new GenericConstraintDeclaration(ParameterName: paramName,
                        ConstraintType: ConstraintKind.ChoiceType,
                        ConstraintTypes: null,
                        Location: location));
                }
                else if (Match(type: TokenType.Variant))
                {
                    inlineConstraints.Add(item: new GenericConstraintDeclaration(ParameterName: paramName,
                        ConstraintType: ConstraintKind.VariantType,
                        ConstraintTypes: null,
                        Location: location));
                }
                else if (Match(type: TokenType.Mutant))
                {
                    inlineConstraints.Add(item: new GenericConstraintDeclaration(ParameterName: paramName,
                        ConstraintType: ConstraintKind.MutantType,
                        ConstraintTypes: null,
                        Location: location));
                }
                else if (Check(type: TokenType.Identifier) || Check(type: TokenType.TypeIdentifier))
                {
                    // Const generic constraint: N is uaddr
                    // Type validation happens in semantic analysis, not parsing
                    TypeExpression constType = ParseType();
                    inlineConstraints.Add(item: new GenericConstraintDeclaration(ParameterName: paramName,
                        ConstraintType: ConstraintKind.ConstGeneric,
                        ConstraintTypes: new List<TypeExpression> { constType },
                        Location: location));
                }
                else
                {
                    throw new ParseException(message: "Expected 'record', 'entity', 'resident', 'routine', 'choice', 'variant', 'mutant', or type after 'is' in inline constraint");
                }
            }
            else if (Match(type: TokenType.In))
            {
                // T in [s32, s64, u32] - type equality constraint with list syntax
                Consume(type: TokenType.LeftBracket, errorMessage: "Expected '[' after 'in' for type equality constraint");

                var equalityTypes = new List<TypeExpression>();
                do
                {
                    equalityTypes.Add(item: ParseType());
                } while (Match(type: TokenType.Comma));

                Consume(type: TokenType.RightBracket, errorMessage: "Expected ']' after type list");

                inlineConstraints.Add(item: new GenericConstraintDeclaration(ParameterName: paramName,
                    ConstraintType: ConstraintKind.TypeEquality,
                    ConstraintTypes: equalityTypes,
                    Location: location));
            }
            // No constraint for this parameter, continue
        } while (Match(type: TokenType.Comma));

        return (genericParams, inlineConstraints.Count > 0
            ? inlineConstraints
            : null);
    }

    /// <summary>
    /// Parses generic constraints for type parameters.
    /// Supports inline constraints (T follows Protocol) and where clauses.
    /// </summary>
    private List<GenericConstraintDeclaration>? ParseGenericConstraints(List<string>? genericParams, List<GenericConstraintDeclaration>? existingConstraints = null)
    {
        if (genericParams == null || genericParams.Count == 0)
        {
            return existingConstraints;
        }

        List<GenericConstraintDeclaration> constraints = existingConstraints != null
            ? new List<GenericConstraintDeclaration>(collection: existingConstraints)
            : new List<GenericConstraintDeclaration>();

        // Parse requires clauses: requires T follows Protocol requires U is record
        // Each parameter can have its own requires clause or they can be comma-separated
        while (Match(type: TokenType.Requires))
        {
            do
            {
                SourceLocation location = GetLocation();
                string paramName = ConsumeIdentifier(errorMessage: "Expected type parameter name");

                // Verify this parameter was declared
                if (!genericParams.Contains(item: paramName))
                {
                    throw new ParseException(message: $"Type parameter '{paramName}' not declared in generic parameters");
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

                    constraints.Add(item: new GenericConstraintDeclaration(ParameterName: paramName,
                        ConstraintType: ConstraintKind.Follows,
                        ConstraintTypes: constraintTypes,
                        Location: location));
                }
                else if (Match(type: TokenType.Is))
                {
                    // T is record/entity/resident/routine/choice/variant/mutant or N is uaddr (const generic)
                    if (Match(type: TokenType.Record))
                    {
                        constraints.Add(item: new GenericConstraintDeclaration(ParameterName: paramName,
                            ConstraintType: ConstraintKind.ValueType,
                            ConstraintTypes: null,
                            Location: location));
                    }
                    else if (Match(type: TokenType.Entity))
                    {
                        constraints.Add(item: new GenericConstraintDeclaration(ParameterName: paramName,
                            ConstraintType: ConstraintKind.ReferenceType,
                            ConstraintTypes: null,
                            Location: location));
                    }
                    else if (Match(type: TokenType.Resident))
                    {
                        constraints.Add(item: new GenericConstraintDeclaration(ParameterName: paramName,
                            ConstraintType: ConstraintKind.ResidentType,
                            ConstraintTypes: null,
                            Location: location));
                    }
                    else if (Match(type: TokenType.Routine))
                    {
                        constraints.Add(item: new GenericConstraintDeclaration(ParameterName: paramName,
                            ConstraintType: ConstraintKind.RoutineType,
                            ConstraintTypes: null,
                            Location: location));
                    }
                    else if (Match(type: TokenType.Choice))
                    {
                        constraints.Add(item: new GenericConstraintDeclaration(ParameterName: paramName,
                            ConstraintType: ConstraintKind.ChoiceType,
                            ConstraintTypes: null,
                            Location: location));
                    }
                    else if (Match(type: TokenType.Variant))
                    {
                        constraints.Add(item: new GenericConstraintDeclaration(ParameterName: paramName,
                            ConstraintType: ConstraintKind.VariantType,
                            ConstraintTypes: null,
                            Location: location));
                    }
                    else if (Match(type: TokenType.Mutant))
                    {
                        constraints.Add(item: new GenericConstraintDeclaration(ParameterName: paramName,
                            ConstraintType: ConstraintKind.MutantType,
                            ConstraintTypes: null,
                            Location: location));
                    }
                    else if (Check(type: TokenType.Identifier) || Check(type: TokenType.TypeIdentifier))
                    {
                        // Const generic constraint: N is uaddr
                        // Type validation happens in semantic analysis, not parsing
                        TypeExpression constType = ParseType();
                        constraints.Add(item: new GenericConstraintDeclaration(ParameterName: paramName,
                            ConstraintType: ConstraintKind.ConstGeneric,
                            ConstraintTypes: new List<TypeExpression> { constType },
                            Location: location));
                    }
                    else
                    {
                        throw new ParseException(message: "Expected 'record', 'entity', 'resident', 'routine', 'choice', 'variant', 'mutant', or type after 'is' in constraint");
                    }
                }
                else if (Match(type: TokenType.In))
                {
                    // T in [s32, s64, u32] - type equality constraint with list syntax
                    Consume(type: TokenType.LeftBracket, errorMessage: "Expected '[' after 'in' for type equality constraint");

                    var equalityTypes = new List<TypeExpression>();
                    do
                    {
                        equalityTypes.Add(item: ParseType());
                    } while (Match(type: TokenType.Comma));

                    Consume(type: TokenType.RightBracket, errorMessage: "Expected ']' after type list");

                    constraints.Add(item: new GenericConstraintDeclaration(ParameterName: paramName,
                        ConstraintType: ConstraintKind.TypeEquality,
                        ConstraintTypes: equalityTypes,
                        Location: location));
                }
                else
                {
                    throw new ParseException(message: "Expected 'follows', 'is', or 'in' in generic constraint");
                }

                // Continue parsing if there's a comma
            } while (Match(type: TokenType.Comma));
        }

        return constraints.Count > 0
            ? constraints
            : null;
    }
}
