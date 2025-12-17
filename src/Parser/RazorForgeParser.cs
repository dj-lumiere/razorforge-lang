using Compilers.Shared.AST;
using Compilers.Shared.Parser;
using Compilers.Shared.Lexer;

namespace Compilers.RazorForge.Parser;

/// <summary>
/// Parser for RazorForge language
/// </summary>
public partial class RazorForgeParser : BaseParser
{
    #region field and property definitions


    private bool _inWhenPatternContext = false;

    private bool
        _inWhenClauseBody = false; // Prevents 'is' expression parsing in when clause bodies

    private bool
        _parsingInlineConditional = false; // Prevents nested inline conditionals (if-then-else expressions)

    #endregion

    public RazorForgeParser(List<Token> tokens, string? fileName = null) : base(tokens: tokens, fileName: fileName ?? "")
    {
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

        // Preset (compile-time constant)
        if (Match(type: TokenType.Preset))
        {
            return ParsePresetDeclaration();
        }

        // Parse attributes (e.g., @crash_only, @inline, @config)
        List<string> attributes = ParseAttributes();

        // Parse visibility modifier
        VisibilityModifier visibility = ParseVisibilityModifier();

        // Imported declaration with optional calling convention
        // Supports: external routine foo() or external("C") routine foo()
        if (visibility == VisibilityModifier.External)
        {
            string? callingConvention = null;

            // Check for calling convention: external("C")
            if (Match(type: TokenType.LeftParen))
            {
                if (Check(TokenType.TextLiteral, TokenType.Text8Literal))
                {
                    Token conventionToken = Advance();
                    // Remove quotes from the text literal
                    callingConvention = conventionToken.Text.Trim(trimChar: '"');
                }

                Consume(type: TokenType.RightParen,
                    errorMessage: "Expected ')' after calling convention");
            }

            if (Match(type: TokenType.Routine))
            {
                return ParseExternalDeclaration(callingConvention: callingConvention);
            }
        }

        // Variable declarations
        if (Match(TokenType.Var, TokenType.Let))
        {
            return ParseVariableDeclaration(visibility: visibility);
        }

        // Pass statement (empty placeholder in records/protocols)
        if (Match(type: TokenType.Pass))
        {
            ConsumeStatementTerminator();
            return new PassStatement(Location: GetLocation());
        }

        // Field declaration in records: public name: Type or name: Type
        // Detected by identifier followed by colon (no var/let keyword needed)
        if (Check(type: TokenType.Identifier) && PeekToken(offset: 1)
               .Type == TokenType.Colon)
        {
            return ParseFieldDeclaration(visibility: visibility);
        }

        // Function declaration
        if (Match(type: TokenType.Routine))
        {
            return ParseFunctionDeclaration(visibility: visibility, attributes: attributes);
        }

        // Entity/Record/Enum declarations
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
            return ParseEnumDeclaration(visibility: visibility);
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

        // If we parsed a visibility modifier but no declaration follows, reset position
        if (visibility != VisibilityModifier.Private)
        {
            // Go back to before the visibility modifier
            Position--;
        }

        // Otherwise parse as statement
        return ParseStatement();
    }

    private Statement? ParseStatement()
    {
        // Control flow
        if (Match(type: TokenType.If))
        {
            return ParseIfStatement();
        }

        if (Match(type: TokenType.Unless))
        {
            return ParseUnlessStatement();
        }

        if (Match(type: TokenType.While))
        {
            return ParseWhileStatement();
        }

        if (Match(type: TokenType.Loop))
        {
            return ParseLoopStatement();
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

        if (Match(type: TokenType.Break))
        {
            return ParseBreakStatement();
        }

        if (Match(type: TokenType.Continue))
        {
            return ParseContinueStatement();
        }

        if (Match(type: TokenType.Throw))
        {
            return ParseThrowStatement();
        }

        if (Match(type: TokenType.Absent))
        {
            return ParseAbsentStatement();
        }

        // Danger block
        if (Match(type: TokenType.Danger))
        {
            return ParseDangerStatement();
        }

        // Viewing block (scoped read-only access)
        if (Match(type: TokenType.Viewing))
        {
            return ParseViewingStatement();
        }

        // Hijacking block (scoped exclusive access)
        if (Match(type: TokenType.Hijacking))
        {
            return ParseHijackingStatement();
        }

        // Inspecting block (thread-safe scoped read access)
        if (Match(type: TokenType.Inspecting))
        {
            return ParseObservingStatement();
        }

        // Seizing block (thread-safe scoped exclusive access)
        if (Match(type: TokenType.Seizing))
        {
            return ParseSeizingStatement();
        }

        // Block statement
        if (Check(type: TokenType.LeftBrace))
        {
            return ParseBlockStatement();
        }

        // Expression statement
        return ParseExpressionStatement();
    }
}
