using System.Numerics;
using Compilers.Shared.AST;
using Compilers.Shared.Lexer;
using Compilers.Shared.Parser;
using Compilers.RazorForge.Parser;

namespace Compilers.Suflae.Parser;

/// <summary>
/// Parser for Suflae language (indentation-based syntax)
/// Handles Python-like indentation with colons and blocks
/// </summary>
public partial class SuflaeParser
{
    #region Base Parser Fields

    /// <summary>
    /// The list of tokens to parse.
    /// </summary>
    protected readonly List<Token> Tokens;

    /// <summary>
    /// Current position in the token stream.
    /// </summary>
    protected int Position = 0;

    /// <summary>
    /// Collection of warnings generated during parsing.
    /// </summary>
    protected readonly List<CompileWarning> Warnings = new();

    /// <summary>
    /// The source file name for error reporting.
    /// </summary>
    public string fileName = "";

    #endregion

    #region Suflae-specific Fields

    /// <summary>
    /// Stack tracking indentation levels for block detection.
    /// </summary>
    private readonly Stack<int> _indentationStack = new();

    /// <summary>
    /// Current indentation level being parsed.
    /// </summary>
    private int _currentIndentationLevel = 0;

    /// <summary>
    /// Prevents nested inline conditionals (if-then-else expressions).
    /// </summary>
    private bool _parsingInlineConditional = false;

    /// <summary>
    /// Indicates whether we're currently parsing inside a record body.
    /// When true, allows field declarations without var/let keywords.
    /// </summary>
    private bool _parsingRecordBody = false;

    /// <summary>
    /// Indicates whether we are currently parsing within a 'when' pattern context.
    /// Used to disambiguate pattern matching syntax from regular expressions.
    /// </summary>
    private bool _inWhenPatternContext;

    /// <summary>
    /// Prevents 'is' expression parsing in when clause bodies.
    /// When true, 'is' is not treated as a pattern-matching operator.
    /// </summary>
    private bool _inWhenClauseBody;

    /// <summary>
    /// Set of known type names for generic disambiguation.
    /// Contains simple names like "List", "User" that have been declared or imported.
    /// </summary>
    private readonly HashSet<string> _knownTypeNames = [];

    /// <summary>
    /// Set of imported namespace names for qualified type resolution.
    /// Contains namespace names like "Collections", "core" from import statements.
    /// </summary>
    private readonly HashSet<string> _importedNamespaces = [];

    /// <summary>
    /// Stack of generic parameter scopes for type name resolution.
    /// Each scope contains parameter names like "K", "V" that are valid within that context.
    /// Pushed when entering a generic declaration, popped when exiting.
    /// </summary>
    private readonly Stack<HashSet<string>> _genericParameterScopes = new();

    #endregion

    /// <summary>
    /// Checks if an identifier is a known type name (declared, imported, or generic parameter).
    /// Used for generic disambiguation when parsing expressions like <c>x &lt; y</c> vs <c>List&lt;T&gt;</c>.
    /// </summary>
    /// <param name="name">The identifier name to check.</param>
    /// <returns>True if the name is a known type; false otherwise.</returns>
    private bool IsKnownTypeName(string name)
    {
        // Check simple type names (declared locally or individually imported)
        if (_knownTypeNames.Contains(item: name))
        {
            return true;
        }

        // Check generic parameter scopes (e.g., K, V within Dict<K, V>)
        foreach (HashSet<string> scope in _genericParameterScopes)
        {
            if (scope.Contains(item: name))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Creates a new Suflae parser for the given token stream.
    /// </summary>
    /// <param name="tokens">The tokens to parse.</param>
    /// <param name="fileName">Optional source file name for error reporting.</param>
    public SuflaeParser(List<Token> tokens, string? fileName = null)
    {
        Tokens = tokens;
        this.fileName = fileName ?? "";
        _indentationStack.Push(item: 0); // Base indentation level
    }

    /// <summary>
    /// Parses the token stream into a complete program AST.
    /// Main entry point for parsing Suflae source files.
    /// </summary>
    /// <returns>A <see cref="Compilers.Shared.AST.Program"/> containing all top-level declarations.</returns>
    public Compilers.Shared.AST.Program Parse()
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
                string location = !string.IsNullOrEmpty(value: fileName)
                    ? $"[{fileName}:{errorToken.Line}:{errorToken.Column}]"
                    : $"[{errorToken.Line}:{errorToken.Column}]";
                Console.Error.WriteLine(value: $"Parse error{location}: {ex.Message}");
                Synchronize();
            }
        }

        return new Compilers.Shared.AST.Program(Declarations: declarations, Location: GetLocation());
    }

    /// <summary>
    /// Parses a single top-level or nested declaration.
    /// Handles: namespace, import, define, using, var/let, routine, entity, record, choice, variant, protocol, impl.
    /// </summary>
    /// <returns>The parsed declaration node, or null if no valid declaration.</returns>
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
            return ParseDefineDeclaration();
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

        // Parse attributes (e.g., @inline, @crash_only, @intrinsic("name"))
        List<string> attributes = ParseAttributes();

        // Parse visibility modifier (with optional setter visibility)
        (VisibilityModifier getterVisibility, VisibilityModifier? setterVisibility) = ParseGetterSetterVisibility();

        // Field declaration in records: public name: Type or name: Type
        // Detected by identifier followed by colon (no var/let keyword needed)
        // Only allowed inside record bodies
        if (_parsingRecordBody && Check(type: TokenType.Identifier) && PeekToken(offset: 1)
               .Type == TokenType.Colon)
        {
            return ParseFieldDeclaration(visibility: getterVisibility, setterVisibility: setterVisibility);
        }

        // Variable declarations
        if (Match(TokenType.Var, TokenType.Let, TokenType.Preset))
        {
            return ParseVariableDeclaration(visibility: getterVisibility, setterVisibility: setterVisibility);
        }

        // Routine (function) declaration - using 'routine' keyword in Suflae
        if (Match(type: TokenType.Routine))
        {
            return ParseRoutineDeclaration(visibility: getterVisibility, attributes: attributes);
        }

        // Entity/Record/Choice declarations
        if (Match(type: TokenType.Entity))
        {
            return ParseEntityDeclaration(visibility: getterVisibility);
        }

        if (Match(type: TokenType.Record))
        {
            return ParseRecordDeclaration(visibility: getterVisibility);
        }

        if (Match(type: TokenType.Choice))
        {
            return ParseChoiceDeclaration(visibility: getterVisibility);
        }

        if (Match(type: TokenType.Variant))
        {
            return ParseVariantDeclaration(visibility: getterVisibility, kind: VariantKind.Variant);
        }

        if (Match(type: TokenType.Protocol))
        {
            return ParseProtocolDeclaration(visibility: getterVisibility);
        }

        // If we parsed a visibility modifier but no declaration follows, it's an error (unless
        // it is an record or protocol)
        if (getterVisibility != VisibilityModifier.Public)
        {
            throw new ParseException(message: $"Visibility modifier '{getterVisibility}' must be followed by a declaration " + $"(routine, entity, record, choice, variant, protocol, var, preset, or let)");
        }

        // If we have attributes but no declaration, that's an error
        if (attributes.Count > 0)
        {
            throw new ParseException(message: $"Attributes must be followed by a declaration (routine, entity, record, etc.)");
        }

        // Otherwise parse as statement
        return ParseStatement();
    }

    /// <summary>
    /// Parses a single statement within a block or function body.
    /// Handles: if, while, for, when, return, throw, absent, break, continue, and expression statements.
    /// </summary>
    /// <returns>The parsed statement, or null if at end of block.</returns>
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

        if (Match(type: TokenType.Throw))
        {
            return ParseThrowStatement();
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

        if (Match(type: TokenType.Pass))
        {
            return ParsePassStatement();
        }

        // Variable declarations (can appear in statement context)
        if (Match(TokenType.Var, TokenType.Let, TokenType.Preset))
        {
            // Check if this is destructuring: let (a, b) = expr or var (x, y) = expr
            if (Check(type: TokenType.LeftParen))
            {
                return ParseDestructuringDeclaration();
            }

            VariableDeclaration varDecl = ParseVariableDeclaration();
            return new ExpressionStatement(Expression: new IdentifierExpression(Name: $"var {varDecl.Name}", Location: GetLocation()), Location: GetLocation());
        }

        // Expression statement
        return ParseExpressionStatement();
    }
}
