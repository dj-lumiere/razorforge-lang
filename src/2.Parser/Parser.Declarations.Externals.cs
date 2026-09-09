using Compiler.Diagnostics;
using Compiler.Tokenizer;
using SyntaxTree;
using TypeModel.Enums;

namespace Compiler.Parser;

/// <summary>
/// Partial class containing external block and external routine declaration parsing.
/// </summary>
public partial class Parser
{
    private ExternalDeclaration ParseExternalDeclaration(string? callingConvention = null,
        List<string>? annotations = null, bool isDangerous = false)
    {
        if (_language == Language.Suflae)
        {
            throw ThrowParseError(code: GrammarDiagnosticCode.RfOnlyConstruct,
                message: "External declarations are only available in RazorForge.");
        }

        // -2 because we consumed 'external' and 'routine'
        SourceLocation location = GetLocation(token: PeekToken(offset: -2));

        _routineNameWired = false;
        if (Match(type: TokenType.Dollar))
        {
            _routineNameWired = true;
        }

        string name = ParseExternalRoutineName();

        // Support ! suffix for failable routines. The `!` is a STRUCTURED flag on the
        // ExternalDeclaration — the name stays bare.
        bool isFailable = Match(type: TokenType.Bang);

        // Check for generic parameters with inline constraints
        List<string>? genericParams = null;
        List<GenericConstraintDeclaration>? inlineConstraints = null;
        if (Match(type: TokenType.LeftBracket))
        {
            (List<string> genericParams, List<GenericConstraintDeclaration>? inlineConstraints)
                result = ParseGenericParametersWithConstraints();
            genericParams = result.genericParams;
            inlineConstraints = result.inlineConstraints;

            Consume(type: TokenType.RightBracket,
                errorMessage: "Expected ']' after generic parameters");
        }

        // Parameters
        Consume(type: TokenType.LeftParen, errorMessage: "Expected '(' after routine name");
        var parameters = new List<Parameter>();
        bool isVariadic = ParseExternalParameters(parameters: parameters);

        Consume(type: TokenType.RightParen, errorMessage: "Expected ')' after parameters");

        // Return type
        TypeExpression? returnType = null;
        if (Match(type: TokenType.Arrow))
        {
            returnType = ParseType();
        }

        // Parse generic constraints (where clause) - merge with inline constraints
        List<GenericConstraintDeclaration>? constraints =
            ParseGenericConstraints(genericParams: genericParams,
                existingConstraints: inlineConstraints);

        ConsumeStatementTerminator();

        // Default to "C" calling convention if not specified
        string effectiveCallingConvention = callingConvention ?? "C";

        return new ExternalDeclaration(Name: name,
            GenericParameters: genericParams,
            GenericConstraints: constraints,
            Parameters: parameters,
            ReturnType: returnType,
            CallingConvention: effectiveCallingConvention,
            IsVariadic: isVariadic,
            Annotations: annotations,
            IsDangerous: isDangerous,
            Location: location)
        {
            IsFailable = isFailable
        };
    }

    /// <summary>
    /// Parses an external routine's (possibly dot-qualified) name, e.g. <c>IO/Console.print</c>.
    /// </summary>
    private string ParseExternalRoutineName()
    {
        var nameSb = new System.Text.StringBuilder(
            ConsumeIdentifier(errorMessage: "Expected routine name"));

        // Support slash-based module paths with a dot-qualified routine name like IO/Console.print
        while (Match(type: TokenType.Dot))
        {
            nameSb.Append('.');
            nameSb.Append(ConsumeIdentifier(errorMessage: "Expected identifier after '.'"));
        }

        return nameSb.ToString();
    }

    /// <summary>
    /// Parses the parameter list of an external routine into <paramref name="parameters"/> (the opening
    /// <c>(</c> is already consumed; the closing <c>)</c> is left for the caller). Returns whether the
    /// list ended with a variadic marker <c>...</c>.
    /// </summary>
    private bool ParseExternalParameters(List<Parameter> parameters)
    {
        bool isVariadic = false;

        if (!Check(type: TokenType.RightParen))
        {
            do
            {
                // Check for variadic marker (...)
                if (Match(type: TokenType.DotDotDot))
                {
                    isVariadic = true;
                    // ... must be last
                    break;
                }

                parameters.Add(item: ParseExternalParameter(parameters: parameters));
            } while (Match(type: TokenType.Comma));
        }

        return isVariadic;
    }

    /// <summary>
    /// Parses a single external routine parameter — a named <c>name: type</c> form (the name is cosmetic
    /// and may be any token, including a keyword) or a type-only form that gets a synthesized positional
    /// placeholder name.
    /// </summary>
    private Parameter ParseExternalParameter(List<Parameter> parameters)
    {
        // A foreign routine's parameters bind by POSITION + TYPE, never by name (C headers carry
        // no caller-visible parameter names, and a foreign call is always @positional). So the
        // name here is purely cosmetic: it may be OMITTED (type-only, `routine C::f(S32, Text)`)
        // or, when present, may be ANY token — including a RazorForge keyword like `flags` that a
        // C header uses — because it never has to be a bindable identifier. The `:` after the
        // first token is the disambiguator: `<anything>: type` is a named param, otherwise the
        // param is type-only.
        if (PeekToken(offset: 1).Type == TokenType.Colon)
        {
            string paramName = Advance().Text; // cosmetic name; keywords allowed
            Consume(type: TokenType.Colon, errorMessage: "Expected ':' after parameter name");
            return new Parameter(Name: paramName, Type: ParseType(), DefaultValue: null,
                Location: GetLocation());
        }

        // Type-only parameter — synthesize a positional placeholder name.
        return new Parameter(Name: $"arg{parameters.Count}", Type: ParseType(),
            DefaultValue: null, Location: GetLocation());
    }

}
