using Compilers.Shared.AST;
using Compilers.Shared.Parser;
using Compilers.Shared.Lexer;

namespace Compilers.Suflae.Parser;

/// <summary>
/// Parser for Suflae language (indentation-based syntax)
/// Handles Python-like indentation with colons and blocks
/// </summary>
public partial class SuflaeParser : BaseParser
{
    #region field and property definitions

    private readonly Stack<int> _indentationStack = new();
    private int _currentIndentationLevel = 0;

    private bool
        _parsingInlineConditional = false; // Prevents nested inline conditionals (if-then-else expressions)

    #endregion

    public SuflaeParser(List<Token> tokens, string? fileName = null) : base(tokens: tokens, fileName: fileName ?? "")
    {
        _indentationStack.Push(item: 0); // Base indentation level
    }



    public override Compilers.Shared.AST.Program Parse()
    {
        var declarations = new List<IAstNode>();

        while (!IsAtEnd)
        {
            try
            {
                // Skip newlines at top level
                if (Match(type: TokenType.Newline))
                {
                    continue;
                }

                // Handle dedent tokens (should not occur at top level, but be safe)
                if (Check(type: TokenType.Dedent))
                {
                    ProcessDedentTokens();
                    continue;
                }

                IAstNode? decl = ParseDeclaration();
                if (decl != null)
                {
                    declarations.Add(item: decl);
                }
            }
            catch (ParseException ex)
            {
                Token errorToken = Position < Tokens.Count
                    ? Tokens[index: Position]
                    : Tokens[^1];
                string location = !string.IsNullOrEmpty(base.fileName)
                    ? $"[{base.fileName}:{errorToken.Line}:{errorToken.Column}]"
                    : $"[{errorToken.Line}:{errorToken.Column}]";
                Console.Error.WriteLine(value: $"Parse error{location}: {ex.Message}");
                Synchronize();
            }
        }

        return new Compilers.Shared.AST.Program(Declarations: declarations,
            Location: GetLocation());
    }

    private IAstNode? ParseDeclaration()
    {
        // Namespace declaration (must appear at top of file)
        if (Match(type: TokenType.Namespace))
        {
            return ParseNamespaceDeclaration();
        }

        // Import declaration
        if (Match(type: TokenType.Import))
        {
            return ParseImportDeclaration();
        }

        // Redefinition
        if (Match(type: TokenType.Define))
        {
            return ParseRedefinitionDeclaration();
        }

        // Using declaration
        if (Match(type: TokenType.Using))
        {
            return ParseUsingDeclaration();
        }

        // Parse visibility modifier
        VisibilityModifier visibility = ParseVisibilityModifier();

        // Variable declarations
        if (Match(TokenType.Var, TokenType.Let))
        {
            return ParseVariableDeclaration(visibility: visibility);
        }

        // Routine (function) declaration - using 'routine' keyword in Suflae
        if (Match(type: TokenType.Routine))
        {
            return ParseRoutineDeclaration(visibility: visibility);
        }

        // Entity/Record/Choice declarations
        if (Match(type: TokenType.Entity))
        {
            return ParseClassDeclaration(visibility: visibility);
        }

        if (Match(type: TokenType.Record))
        {
            return ParseStructDeclaration(visibility: visibility);
        }

        if (Match(type: TokenType.Choice))
        {
            return ParseChoiceDeclaration(visibility: visibility); // choice in Suflae
        }

        if (Match(type: TokenType.Variant))
        {
            return ParseVariantDeclaration(visibility: visibility, kind: VariantKind.Variant);
        }

        if (Match(type: TokenType.Mutant))
        {
            return ParseVariantDeclaration(visibility: visibility, kind: VariantKind.Mutant);
        }

        if (Match(type: TokenType.Protocol))
        {
            return ParseFeatureDeclaration(visibility: visibility);
        }

        // Implementation blocks (Type follows Trait:)
        if (CheckImplementation())
        {
            return ParseImplementationDeclaration();
        }

        // If we parsed a visibility modifier but no declaration follows, reset position
        if (visibility != VisibilityModifier.Private)
        {
            // Go back to before the visibility modifier
            Position--;
        }

        // Otherwise parse as statement
        return ParseStatement();
    }

    private bool CheckImplementation()
    {
        // Look for pattern: Identifier follows Identifier:
        if (Check(type: TokenType.Identifier) || Check(type: TokenType.TypeIdentifier))
        {
            int saved = Position;
            Advance(); // Skip type name
            if (Match(type: TokenType.Follows))
            {
                Position = saved; // Reset position
                return true;
            }

            Position = saved; // Reset position
        }

        return false;
    }

    private Statement? ParseStatement()
    {
        // Handle dedent tokens
        if (Check(type: TokenType.Dedent))
        {
            ProcessDedentTokens();
        }

        // Skip newlines
        while (Match(type: TokenType.Newline)) { }

        if (IsAtEnd)
        {
            return null;
        }

        // Control flow
        if (Match(type: TokenType.If))
        {
            return ParseIfStatement();
        }

        if (Match(type: TokenType.While))
        {
            return ParseWhileStatement();
        }

        if (Match(type: TokenType.For))
        {
            return ParseForStatement();
        }

        if (Match(type: TokenType.When))
        {
            return ParseWhenStatement();
        }

        if (Match(type: TokenType.Return))
        {
            return ParseReturnStatement();
        }

        if (Match(type: TokenType.Throw))
        {
            return ParseFailStatement();
        }

        if (Match(type: TokenType.Absent))
        {
            return ParseAbsentStatement();
        }

        if (Match(type: TokenType.Break))
        {
            return ParseBreakStatement();
        }

        if (Match(type: TokenType.Continue))
        {
            return ParseContinueStatement();
        }

        // Variable declarations (can appear in statement context)
        if (Match(TokenType.Var, TokenType.Let))
        {
            VariableDeclaration varDecl = ParseVariableDeclaration();
            return new ExpressionStatement(
                Expression: new IdentifierExpression(Name: $"var {varDecl.Name}",
                    Location: GetLocation()),
                Location: GetLocation());
        }

        // Suflae's display statement (equivalent to print/console.log)
        if (Check(type: TokenType.Identifier) && CurrentToken.Text == "display")
        {
            Advance();
            Consume(type: TokenType.LeftParen, errorMessage: "Expected '(' after 'display'");
            Expression expr = ParseExpression();
            Consume(type: TokenType.RightParen,
                errorMessage: "Expected ')' after display expression");
            Match(type: TokenType.Newline);

            // Convert to a function call expression
            var displayCall =
                new CallExpression(
                    Callee: new IdentifierExpression(Name: "Console.WriteLine",
                        Location: GetLocation()),
                    Arguments: new List<Expression> { expr },
                    Location: GetLocation());

            return new ExpressionStatement(Expression: displayCall, Location: GetLocation());
        }

        // Expression statement
        return ParseExpressionStatement();
    }
}
