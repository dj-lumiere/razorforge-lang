using Compiler.Diagnostics;
using Compiler.Tokenizer;
using SyntaxTree;
using TypeModel.Enums;

namespace Compiler.Parser;

/// <summary>
/// Unified parser for both RazorForge and Suflae languages.
/// Converts a stream of tokens into an Abstract Syntax Tree (AST).
/// Language-specific constructs are guarded by <see cref="_language"/> checks.
/// </summary>
public partial class Parser
{
    #region Base Parser Fields

    /// <summary>
    /// The list of tokens to parse.
    /// </summary>
    private readonly List<Token> _tokens;

    /// <summary>
    /// Current position in the token stream.
    /// </summary>
    private int _position;

    /// <summary>
    /// Collection of warnings generated during parsing.
    /// </summary>
    private readonly List<BuildWarning> _warnings = [];

    /// <summary>
    /// Collection of errors accumulated during error recovery.
    /// </summary>
    private readonly List<string> _errors = [];

    /// <summary>
    /// Structured form of the accumulated parse errors, preserving code + source position
    /// (the plain <see cref="_errors"/> strings are pre-formatted and lose the location fields).
    /// Consumed by the language server to emit positioned diagnostics.
    /// </summary>
    private readonly List<GrammarException> _structuredErrors = [];

    /// <summary>
    /// Returns true if any parse errors occurred during parsing.
    /// </summary>
    public bool HasErrors => _errors.Count > 0;

    /// <summary>
    /// Gets all parse errors encountered during parsing.
    /// </summary>
    public List<string> GetErrors()
    {
        return _errors;
    }

    /// <summary>
    /// Gets the accumulated parse errors with their code and source position preserved.
    /// </summary>
    public IReadOnlyList<GrammarException> GetStructuredErrors()
    {
        return _structuredErrors;
    }

    /// <summary>
    /// The source file name for error reporting.
    /// </summary>
    public string FileName = "";

    /// <summary>
    /// The language being parsed (RazorForge or Suflae).
    /// Used to guard language-specific constructs.
    /// </summary>
    private readonly Language _language;

    #endregion

    #region Indentation Fields

    /// <summary>
    /// Stack tracking indentation levels for block detection.
    /// </summary>
    private readonly Stack<int> _indentationStack = new();

    /// <summary>
    /// Current indentation level being parsed.
    /// </summary>
    private int _currentIndentationLevel;

    #endregion

    #region Guarded Parser State

    /// <summary>
    /// Prevents nested inline conditionals (if-then-else expressions).
    /// When true, 'if' at expression level is not parsed as inline conditional.
    /// This improves readability by forbidding constructs like:
    /// <c>if a then (if b then c else d) else e</c>
    /// </summary>
    private bool _parsingInlineConditional;

    /// <summary>
    /// Indicates whether we're currently parsing inside a type body (record, entity).
    /// When true, allows member variable declarations without var keywords.
    /// </summary>
    private bool _parsingTypeBody;

    /// <summary>
    /// Indicates whether we're parsing inside a record body (actual record, not entity).
    /// When true, only secret/posted/open modifiers are allowed (not external).
    /// Also var/preset keywords are disallowed (use 'name: Type' syntax).
    /// </summary>
    private bool _parsingStrictRecordBody;

    /// <summary>
    /// Indicates whether we're currently parsing inside a routine body.
    /// When true, nested routine declarations are rejected.
    /// </summary>
    private bool _inRoutineBody;

    /// <summary>
    /// Indicates whether we are currently parsing within a 'when' pattern context.
    /// Used to disambiguate pattern matching syntax from regular expressions.
    /// </summary>
    private bool _inWhenPatternContext;

    /// <summary>
    /// Indicates we are parsing the condition of a subjectless (condition-based) 'when' arm.
    /// Suppresses bare-identifier lambda parsing so `flag => result` reads as
    /// condition `flag` + arm arrow, not lambda `flag => result`. Unlike
    /// _inWhenPatternContext it leaves the 'is' operator available, and it is
    /// suspended inside parentheses and argument lists so explicit lambdas there
    /// still parse.
    /// </summary>
    private bool _inWhenConditionContext;

    /// <summary>
    /// The reserved parameter name a single-hole `_` lambda desugars to. `xs.map(_ * 2)` parses the
    /// `_` as a reference to this name and wraps the whole argument in `LambdaExpression([<hole>], _*2)`.
    /// </summary>
    internal const string HoleParamName = "__rf_hole";

    /// <summary>
    /// Set by <c>ParsePrimary</c> when it parses a bare `_` placeholder (single-hole lambda). Read and
    /// reset per-argument by <c>ParseArgument</c>, which wraps the argument into a lambda when set — so
    /// the lambda boundary is the nearest enclosing argument.
    /// </summary>
    private bool _sawHole;

    /// <summary>
    /// Prevents 'is' expression parsing in when clause bodies.
    /// When true, 'is' is not treated as a pattern-matching operator.
    /// </summary>
    private bool _inWhenClauseBody;

    #endregion

    /// <summary>
    /// Creates a new unified parser for the given token stream and language.
    /// </summary>
    /// <param name="tokens">The tokens to parse.</param>
    /// <param name="language">The language being parsed (RazorForge or Suflae).</param>
    /// <param name="fileName">Optional source file name for error reporting.</param>
    public Parser(List<Token> tokens, Language language, string? fileName = null)
    {
        _tokens = tokens;
        _language = language;
        FileName = fileName ?? "unknown";
        _indentationStack.Push(item: 0); // Base indentation level
    }

    /// <summary>
    /// Parses the token stream into a complete program AST.
    /// Main entry point for parsing source files.
    /// </summary>
    /// <returns>A <see cref="SyntaxTree.Program"/> containing all top-level declarations.</returns>
    public Program Parse()
    {
        var declarations = new List<ISyntaxTreeNode>();

        // Doc-comment (### ...) lines accumulate here until the next declaration claims them.
        var pendingDoc = new List<string>();

        while (!IsAtEnd)
        {
            try
            {
                // Skip newlines at top level
                if (Match(type: TokenType.Newline))
                {
                    continue;
                }

                // Accumulate doc-comment (### ...) lines; they attach to the NEXT declaration. A module
                // file may also end with a block of documentation that declares nothing (e.g. the
                // BuilderExpansion capability-gate module) — that trailing block simply never gets claimed
                // and is discarded when the loop ends, so it can't drive ParseDeclaration into an EOF error.
                if (Check(type: TokenType.DocComment))
                {
                    pendingDoc.Add(item: Advance().Text.Trim());
                    continue;
                }

                // Handle dedent tokens (should not occur at top level, but be safe)
                if (Check(type: TokenType.Dedent))
                {
                    ProcessDedentTokens();
                    continue;
                }

                ISyntaxTreeNode decl = ParseDeclaration();
                if (pendingDoc.Count > 0 && decl is SyntaxTree.Declaration documented)
                {
                    documented.Documentation = string.Join(separator: "\n", values: pendingDoc);
                }

                pendingDoc.Clear();
                declarations.Add(item: decl);
            }
            catch (GrammarException ex)
            {
                // GrammarException.Message already contains formatted error:
                // error[RF-G150]: filename.rf:9:14: message
                // error[SF-G150]: filename.sf:9:14: message
                _errors.Add(item: ex.Message);
                _structuredErrors.Add(item: ex);
                DiagnosticRenderer.Print(ex: ex, writer: Console.Error);
                Synchronize();
            }
        }

        if (_language == Language.Suflae)
        {
            declarations = WrapScriptStatementsIntoStart(nodes: declarations);
        }

        return new Program(Declarations: declarations, Location: GetLocation());
    }

    /// <summary>
    /// Suflae "script mode": a file whose top level has loose STATEMENTS (an expression, `each`/`while`/
    /// `if`, an assignment, …) needs no explicit entry point — those statements, together with any top-level
    /// runtime <c>var</c> declarations, become the body of an implicit <c>routine start()</c> (in source
    /// order; a trailing <c>return</c> is added). Hoistable declarations (module/import/type/routine/preset/
    /// define) stay as siblings and hoist as usual. A no-op for a normal module file (no loose statements).
    /// An explicit <c>start</c> alongside top-level statements is a conflict.
    /// </summary>
    private List<ISyntaxTreeNode> WrapScriptStatementsIntoStart(List<ISyntaxTreeNode> nodes)
    {
        // Trigger only on a loose top-level STATEMENT — a pure module file (declarations only) is untouched.
        bool hasLooseStatement = false;
        foreach (ISyntaxTreeNode n in nodes)
        {
            if (n is Statement)
            {
                hasLooseStatement = true;
                break;
            }
        }

        if (!hasLooseStatement)
        {
            return nodes;
        }

        // Collect the executable top-level nodes (statements + runtime var decls) in source order.
        var body = new List<Statement>();
        var kept = new List<ISyntaxTreeNode>();
        SourceLocation startLoc = PartitionScriptNodes(nodes: nodes, body: body, kept: kept,
            explicitStart: out RoutineDeclaration? explicitStart);

        if (explicitStart != null)
        {
            return ReportScriptStartConflict(kept: kept, startLoc: startLoc);
        }

        if (body.Count == 0 || body[^1] is not ReturnStatement)
        {
            body.Add(item: new ReturnStatement(Value: null, Location: startLoc));
        }

        kept.Add(item: new RoutineDeclaration(
            Name: "start",
            Parameters: [],
            ReturnType: null,
            Body: new BlockStatement(Statements: body, Location: startLoc),
            Visibility: VisibilityModifier.Open,
            Annotations: [],
            Location: startLoc));
        return kept;
    }

    /// <summary>
    /// Splits the top-level nodes into the implicit-<c>start</c> body (loose statements + runtime var
    /// declarations, wrapped) and the kept hoistable declarations, in source order. Records any explicit
    /// <c>routine start()</c> found via <paramref name="explicitStart"/>. Returns the source location of
    /// the first executable node (or a default location when there is none).
    /// </summary>
    private SourceLocation PartitionScriptNodes(List<ISyntaxTreeNode> nodes, List<Statement> body,
        List<ISyntaxTreeNode> kept, out RoutineDeclaration? explicitStart)
    {
        SourceLocation startLoc = GetLocation();
        bool locSet = false;
        explicitStart = null;
        foreach (ISyntaxTreeNode n in nodes)
        {
            switch (n)
            {
                case Statement s:
                    if (!locSet) { startLoc = s.Location; locSet = true; }
                    body.Add(item: s);
                    break;
                case VariableDeclaration vd:
                    if (!locSet) { startLoc = vd.Location; locSet = true; }
                    body.Add(item: new DeclarationStatement(Declaration: vd, Location: vd.Location));
                    break;
                default:
                    if (n is RoutineDeclaration { Name: "start" } rd) { explicitStart = rd; }
                    kept.Add(item: n);
                    break;
            }
        }

        return startLoc;
    }

    /// <summary>
    /// Reports the conflict between top-level statements and an explicit <c>routine start()</c> as a
    /// clean diagnostic (matching the per-statement error path) rather than throwing out of the parser,
    /// and returns the kept declarations so the Program stays well-formed.
    /// </summary>
    private List<ISyntaxTreeNode> ReportScriptStartConflict(List<ISyntaxTreeNode> kept,
        SourceLocation startLoc)
    {
        var ex = new GrammarException(code: GrammarDiagnosticCode.UnexpectedToken,
            message:
            "A Suflae file cannot mix top-level statements with an explicit `routine start()`. " +
            "Either move the top-level statements into start(), or remove the explicit start().",
            fileName: FileName, line: startLoc.Line, column: startLoc.Column, language: _language);
        _errors.Add(item: ex.Message);
        _structuredErrors.Add(item: ex);
        DiagnosticRenderer.Print(ex: ex, writer: Console.Error);
        return kept;
    }

    /// <summary>
    /// Parses a single top-level or nested declaration.
    /// Handles: module, import, define, using, var, routine, entity, record, choice, variant, protocol, impl.
    /// RazorForge-only: external, dangerous modifier, threaded async status.
    /// </summary>
    /// <remarks>
    /// Declaration parsing order (checked in sequence):
    ///
    /// FILE-LEVEL DECLARATIONS (must appear first):
    ///   module       - Module declaration
    ///   import       - Import external modules
    ///   define       - Type alias/redefinition
    ///   preset       - Build-time constant
    ///
    /// MODIFIERS (optional, parsed before declaration):
    ///   annotations   - @crash_only, @inline, @llvm("i32"), etc.
    ///   visibility   - secret, posted, open, external
    ///   storage      - common, global
    ///
    /// RF-ONLY MODIFIERS:
    ///   dangerous    - Marks routine as unsafe (RazorForge only)
    ///
    /// TYPE/VALUE DECLARATIONS:
    ///   external     - FFI routine declaration (RazorForge only)
    ///   name: Type  - Member variable declaration (inside type bodies)
    ///   var          - Variable declarations
    ///   pass         - Empty placeholder (RazorForge only)
    ///   routine      - FreeRoutine declaration
    ///   entity       - Heap-allocated reference type
    ///   record       - Stack-allocated value type
    ///   choice       - Simple enumeration
    ///   variant      - Tagged union (sum type)
    ///   protocol     - Interface/trait definition
    ///
    /// SPECIAL DECLARATION:
    ///   using        - Resource management (declaration form, no body block)
    ///
    /// If no declaration keyword matches, falls through to ParseStatement.
    /// </remarks>
    /// <returns>The parsed declaration node.</returns>
    /// <exception cref="GrammarException">Thrown when no valid declaration or statement can be parsed.</exception>
    private ISyntaxTreeNode ParseDeclaration()
    {
        // ═══════════════════════════════════════════════════════════════════════════
        // SKIP DOC COMMENTS (### comment lines before declarations)
        // ═══════════════════════════════════════════════════════════════════════════
        // Doc comments are preserved in the token stream but currently not attached
        // to declarations. Skip them to prevent "Unexpected token" errors.
        while (Match(type: TokenType.DocComment))
        {
            // Skip any newlines after doc comments
            while (Match(type: TokenType.Newline)) { } // NOSONAR S108: intentional newline-consuming loop
        }

        // A leading `@target(...)` build directive (file-granularity conditional compilation) precedes
        // `module`. It is read PRE-PARSE by the build's file gate to decide whether to compile this file
        // at all; by the time the parser sees it the file is already selected, so here it is consumed and
        // discarded (its effect happened earlier). Keeping it a real `@`-annotation — not a comment — is
        // what gives it editor highlighting.
        if (Check(type: TokenType.At) && PeekToken(offset: 1).Type == TokenType.Identifier
            && PeekToken(offset: 1).Text == "target")
        {
            ParseAnnotations();
            while (Match(type: TokenType.Newline)) { } // NOSONAR S108
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // FILE-LEVEL DECLARATIONS (must appear at top of file)
        // ═══════════════════════════════════════════════════════════════════════════

        // Module declaration (must appear at top of file)
        if (Match(type: TokenType.Module))
        {
            return ParseModuleDeclaration();
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

        // Preset (build-time constant)
        if (Match(type: TokenType.Preset))
        {
            return ParsePresetDeclaration();
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // PARSE MODIFIERS (annotations, visibility, storage class)
        // ═══════════════════════════════════════════════════════════════════════════

        // Parse annotations (e.g., @inline, @crash_only, @llvm("i32"))
        List<string> annotations = ParseAnnotations();

        // Skip newlines between annotations and the declaration they modify
        // e.g., @readonly\nroutine foo() should work
        if (annotations.Count > 0)
        {
            while (Match(type: TokenType.Newline))
            {
                // Skip newlines
            }
        }

        // Parse visibility and storage class modifiers
        (VisibilityModifier visibility, bool isCommon) = ParseModifiers();

        // Define declaration with annotations (e.g., @llvm("i32") define MyInt as S32)
        if (Match(type: TokenType.Define))
        {
            return ParseDefineDeclaration(annotations: annotations);
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // RF-ONLY: DANGEROUS MODIFIER
        // ═══════════════════════════════════════════════════════════════════════════

        // Check for dangerous modifier: dangerous routine foo(), dangerous external("C") routine bar()
        // (RazorForge only)
        bool isDangerous = false;
        if (_language == Language.RazorForge)
        {
            isDangerous = Match(type: TokenType.Dangerous);
        }

        // Foreign routines are declared via realm-qualified names — `routine C::name(...)` /
        // `routine LLVM::name(...)` (handled in the routine-declaration dispatch below), which produce an
        // ExternalDeclaration. The old `external("C"|"llvm")` keyword form (incl. the block form) was
        // removed in favor of that spelling.

        // Decl-position expand: `expand m in allmemvarof(T)` inside a record/entity body generates one
        // member-variable column per member of the (concrete-at-instantiation) source type. Used by the
        // struct-of-arrays collections SplitArray/SplitList.
        if (_parsingTypeBody && Check(type: TokenType.Expand))
        {
            return ParseExpandMemberDeclaration();
        }

        // Field declaration in type bodies: name: Type
        // Detected by identifier followed by colon (no var keyword needed)
        // Only allowed inside type bodies (record, entity)
        if (_parsingTypeBody && Check(type: TokenType.Identifier) && PeekToken(offset: 1)
               .Type == TokenType.Colon)
        {
            return ParseTypeBodyFieldDeclaration(visibility: visibility);
        }

        // Variable declarations — optionally prefixed with `lateinit`
        bool declLateInit = false;
        if (Check(type: TokenType.LateInit) && PeekToken(offset: 1).Type == TokenType.Var)
        {
            Advance(); // consume 'lateinit'
            declLateInit = true;
        }
        // `secret preset NAME` (visibility-prefixed preset) — route to the dedicated preset parser so
        // it becomes a PresetDeclaration carrying its secret (file-private) flag, not a VariableDeclaration.
        if (Check(type: TokenType.Preset))
        {
            return ParsePresetInDeclarationPosition(visibility: visibility);
        }
        // `global NAME: Type = value` — module-level mutable global. SUFLAE-ONLY: SF's GC + single-thread
        // + REPL model makes a session-lifetime global honest, whereas RazorForge's deterministic
        // scope-anchored teardown has no owning scope for one (so RF has no module-level mutable state).
        // Also not allowed inside a type body (member variables use bare `name: Type`).
        if (Check(type: TokenType.Global))
        {
            return ParseGlobalInDeclarationPosition(visibility: visibility,
                annotations: annotations);
        }
        if (Match(TokenType.Var))
        {
            // In type bodies (record, entity), var/preset are not allowed
            // MemberVariables use 'name: Type' syntax without var keywords
            if (_parsingTypeBody)
            {
                throw new GrammarException(code: GrammarDiagnosticCode.InvalidDeclarationInBody,
                    message: "Type member variables cannot use 'var' or 'preset'. " +
                             "Use 'name: Type' syntax instead",
                    fileName: FileName,
                    line: CurrentToken.Line,
                    column: CurrentToken.Column,
                    language: _language);
            }

            return ParseVariableDeclaration(visibility: visibility,
                annotations: annotations, isLateInit: declLateInit);
        }

        // Pass statement/declaration (empty placeholder)
        // Inside type bodies, returns PassDeclaration (a Declaration subtype)
        // Outside type bodies, returns PassStatement (a Statement subtype)
        if (_language == Language.RazorForge && Match(type: TokenType.Pass))
        {
            ConsumeStatementTerminator();

            // Inside type bodies, return a PassDeclaration (extends Declaration)
            if (_parsingTypeBody)
            {
                return new PassDeclaration(Location: GetLocation());
            }

            return new PassStatement(Location: GetLocation());
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // ROUTINE DECLARATION (with async status modifiers)
        // ═══════════════════════════════════════════════════════════════════════════

        AsyncStatus asyncStatus = ParseAsyncStatusModifier();

        // Routine (function) declaration
        if (Match(type: TokenType.Routine))
        {
            return ParseRoutineOrForeignDeclaration(visibility: visibility,
                annotations: annotations,
                isCommon: isCommon,
                asyncStatus: asyncStatus,
                isDangerous: isDangerous);
        }

        // If we consumed async modifiers but no 'routine' follows, that's an error
        if (asyncStatus != AsyncStatus.None)
        {
            string modifier = asyncStatus switch
            {
                AsyncStatus.Suspended => "suspended",
                AsyncStatus.Threaded  => "threaded",
                _                     => asyncStatus.ToString().ToLower()
            };
            throw new GrammarException(code: GrammarDiagnosticCode.UnexpectedToken,
                message: $"'{modifier}' must be followed by 'routine'",
                fileName: FileName,
                line: CurrentToken.Line,
                column: CurrentToken.Column,
                language: _language);
        }

        // Validate: storage class modifiers are not valid for type declarations
        RejectCommonOnTypeDeclaration(isCommon: isCommon);

        // Entity/Record/Choice declarations
        if (Match(type: TokenType.Entity))
        {
            return ParseEntityDeclaration(visibility: visibility);
        }

        if (Match(type: TokenType.Record))
        {
            return ParseRecordDeclaration(visibility: visibility, annotations: annotations);
        }

        if (Match(type: TokenType.Choice))
        {
            return ParseChoiceDeclaration(visibility: visibility);
        }

        if (Match(type: TokenType.Flags))
        {
            return ParseFlagsDeclaration(visibility: visibility);
        }

        if (Match(type: TokenType.Crashable))
        {
            return ParseCrashableDeclaration(visibility: visibility);
        }

        if (Match(type: TokenType.Variant))
        {
            return ParseVariantDeclaration();
        }

        if (Match(type: TokenType.Protocol))
        {
            return ParseProtocolDeclaration(visibility: visibility);
        }

        // If we parsed a visibility modifier but no declaration follows, it's an error (unless
        // it is an record or protocol)
        if (visibility != VisibilityModifier.Open)
        {
            string validDeclarations =
                "routine, entity, record, choice, variant, protocol, preset, or var";
            throw ThrowParseError(code: GrammarDiagnosticCode.VisibilityWithoutDeclaration,
                message: $"Visibility modifier '{visibility}' must be followed by a declaration " +
                         $"({validDeclarations})");
        }

        // If we have annotations but no declaration, that's an error
        if (annotations.Count > 0)
        {
            throw ThrowParseError(code: GrammarDiagnosticCode.AnnotationsWithoutDeclaration,
                message:
                "Annotations must be followed by a declaration (routine, entity, record, etc.)");
        }

        // Otherwise parse as statement
        return ParseStatement();
    }

    /// <summary>
    /// Parses a member-variable field declaration (<c>name: Type</c>) inside a type body, after the
    /// leading identifier+colon has been detected. Rejects the <c>external</c> visibility inside a
    /// strict record body.
    /// </summary>
    private ISyntaxTreeNode ParseTypeBodyFieldDeclaration(VisibilityModifier visibility)
    {
        // In record bodies, external is not allowed
        if (_parsingStrictRecordBody && visibility is VisibilityModifier.External)
        {
            throw new GrammarException(code: GrammarDiagnosticCode.InvalidDeclarationInBody,
                message:
                $"'{visibility.ToString().ToLower()}' is not valid for record member variables. " +
                "Record member variables can use 'secret', 'posted', or 'open'",
                fileName: FileName,
                line: CurrentToken.Line,
                column: CurrentToken.Column,
                language: _language);
        }

        return ParseMemberVariableDeclaration(visibility: visibility);
    }

    /// <summary>
    /// Handles a <c>preset</c> keyword encountered in declaration position (after modifiers). Rejects
    /// <c>preset</c> inside a type body and routes to <see cref="ParsePresetDeclaration"/>, carrying the
    /// secret (file-private) flag from the visibility modifier.
    /// </summary>
    private ISyntaxTreeNode ParsePresetInDeclarationPosition(VisibilityModifier visibility)
    {
        if (_parsingTypeBody)
        {
            throw new GrammarException(code: GrammarDiagnosticCode.InvalidDeclarationInBody,
                message: "Type member variables cannot use 'var' or 'preset'. " +
                         "Use 'name: Type' syntax instead",
                fileName: FileName,
                line: CurrentToken.Line,
                column: CurrentToken.Column,
                language: _language);
        }

        Advance(); // consume 'preset'
        return ParsePresetDeclaration(isSecret: visibility == VisibilityModifier.Secret);
    }

    /// <summary>
    /// Handles a <c>global</c> keyword encountered in declaration position. Rejects <c>global</c> in
    /// RazorForge (no module-level mutable state) and inside a type body, then routes to
    /// <see cref="ParseGlobalDeclaration"/>.
    /// </summary>
    private ISyntaxTreeNode ParseGlobalInDeclarationPosition(VisibilityModifier visibility,
        List<string> annotations)
    {
        if (_language == Language.RazorForge)
        {
            throw new GrammarException(code: GrammarDiagnosticCode.InvalidDeclarationInBody,
                message: "'global' is Suflae-only: RazorForge has no module-level mutable state " +
                         "(thread state through parameters or a heap entity; use 'preset' for constants)",
                fileName: FileName,
                line: CurrentToken.Line,
                column: CurrentToken.Column,
                language: _language);
        }
        if (_parsingTypeBody)
        {
            throw new GrammarException(code: GrammarDiagnosticCode.InvalidDeclarationInBody,
                message: "Type member variables cannot use 'global'. Use 'name: Type' syntax instead",
                fileName: FileName,
                line: CurrentToken.Line,
                column: CurrentToken.Column,
                language: _language);
        }

        Advance(); // consume 'global'
        return ParseGlobalDeclaration(visibility: visibility,
            annotations: annotations);
    }

    /// <summary>
    /// Consumes an optional async-status concurrency modifier (<c>threaded</c> — RF only — or
    /// <c>suspended</c>) preceding a <c>routine</c> declaration and returns the resulting status.
    /// </summary>
    private AsyncStatus ParseAsyncStatusModifier()
    {
        // Concurrency modifier: threaded routine foo() (RazorForge only, v0.1)
        if (_language == Language.RazorForge && Match(type: TokenType.Threaded))
        {
            return AsyncStatus.Threaded;
        }
        // Concurrency modifier: suspended routine foo() — a stackful coroutine. SHARED between RF and
        // SF (SUFLAE-FOR-AI §2.8 lists `suspended` as an identical keyword; only `threaded` is RF-only,
        // since SF's Roamed/cycle-collected model has no raw shared-memory threading). SF's single-
        // thread/REPL model is exactly where cooperative coroutines fit.
        if (Match(type: TokenType.Suspended))
        {
            return AsyncStatus.Suspended;
        }

        return AsyncStatus.None;
    }

    /// <summary>
    /// Parses a routine declaration after the <c>routine</c> keyword has been consumed, dispatching to
    /// a realm-qualified FOREIGN routine (<c>routine C::malloc(...)</c> / <c>routine LLVM::sqrt(...)</c>)
    /// when a realm tag before <c>::</c> is present, otherwise an ordinary routine declaration.
    /// </summary>
    private ISyntaxTreeNode ParseRoutineOrForeignDeclaration(VisibilityModifier visibility,
        List<string> annotations, bool isCommon, AsyncStatus asyncStatus, bool isDangerous)
    {
        // Realm-qualified FOREIGN routine: `routine C::malloc(...)` / `routine LLVM::sqrt(...)`. The
        // realm tag before `::` picks the calling convention; the declaration is an ExternalDeclaration
        // (no body, foreign impl) — the modern spelling of `external("C"|"llvm") routine ...`.
        if (Check(type: TokenType.Identifier) &&
            PeekToken(offset: 1).Type == TokenType.DoubleColon)
        {
            string? conv = CurrentToken.Text switch
            {
                "C" => "C",
                "LLVM" => "llvm",
                _ => null
            };
            if (conv != null)
            {
                Advance(); // realm tag (C / LLVM)
                Advance(); // ::
                return ParseExternalDeclaration(callingConvention: conv,
                    annotations: annotations,
                    isDangerous: isDangerous);
            }
        }

        return ParseRoutineDeclaration(visibility: visibility,
            annotations: annotations,
            isCommon: isCommon,
            asyncStatus: asyncStatus,
            isDangerous: isDangerous);
    }

    /// <summary>
    /// Rejects a storage-class modifier (<c>common</c>/<c>global</c>) placed before a type-declaration
    /// keyword (entity/record/choice/flags/crashable/variant/protocol), which is not valid.
    /// </summary>
    private void RejectCommonOnTypeDeclaration(bool isCommon)
    {
        if (!isCommon)
        {
            return;
        }

        bool isTypeKeyword = Check(TokenType.Entity,
            TokenType.Record,
            TokenType.Choice,
            TokenType.Flags,
            TokenType.Crashable,
            TokenType.Variant,
            TokenType.Protocol);

        if (isTypeKeyword)
        {
            throw new GrammarException(code: GrammarDiagnosticCode.InvalidDeclarationInBody,
                message:
                "'common' storage class is not valid for type declarations",
                fileName: FileName,
                line: CurrentToken.Line,
                column: CurrentToken.Column,
                language: _language);
        }
    }

    /// <summary>
    /// Parses a single statement within a block or function body.
    /// Handles: if, while, for, when, return, throw, absent, break, continue, and expression statements.
    /// RazorForge-only: danger block, steal expression, release statement, block statements.
    /// </summary>
    /// <remarks>
    /// Statement types (checked in sequence):
    ///
    /// INDENTATION HANDLING:
    ///   dedent       - Process block end
    ///   newlines     - Skip empty lines
    ///
    /// CONTROL FLOW:
    ///   if/unless    - Conditional branching
    ///   while/loop   - Loop constructs
    ///   for          - Iteration over ranges/collections
    ///   when         - Pattern matching (switch-like)
    ///
    /// JUMP STATEMENTS:
    ///   return       - Return from routine (with optional value)
    ///   becomes      - argument assign with if-elseif-else
    ///   break        - Exit loop
    ///   continue     - Skip to next iteration
    ///
    /// SPECIAL STATEMENTS:
    ///   throw        - Throw error (in failable routines)
    ///   absent       - Return none (in failable routines)
    ///   pass         - Empty placeholder (no-op)
    ///   using        - Resource management (declaration form)
    ///
    /// MEMORY BLOCKS (RazorForge only):
    ///   danger      - Unsafe block (raw pointers, FFI)
    ///   release      - Early resource cleanup
    ///
    /// DECLARATIONS IN STATEMENT CONTEXT:
    ///   var          - Variable declarations (including destructuring)
    ///
    /// BLOCK/EXPRESSION:
    ///   { ... }      - Block statement (RazorForge only)
    ///   expr         - Expression statement (fallback)
    /// </remarks>
    /// <returns>The parsed statement, or null if at end of block.</returns>
    private Statement ParseStatement() // NOSONAR S3776
    {
        // ═══════════════════════════════════════════════════════════════════════════
        // INDENTATION HANDLING
        // ═══════════════════════════════════════════════════════════════════════════

        // Handle dedent tokens
        if (Check(type: TokenType.Dedent))
        {
            ProcessDedentTokens();
        }

        // Skip newlines
        while (Match(type: TokenType.Newline)) { } // NOSONAR S108: intentional newline-consuming loop

        // ═══════════════════════════════════════════════════════════════════════════
        // CONTROL FLOW STATEMENTS
        // ═══════════════════════════════════════════════════════════════════════════

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

        if (Match(type: TokenType.Each))
        {
            return ParseEachStatement();
        }

        if (Match(type: TokenType.Expand))
        {
            return ParseExpandStatement();
        }

        if (Match(type: TokenType.When))
        {
            return ParseWhenStatement();
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // JUMP STATEMENTS
        // ═══════════════════════════════════════════════════════════════════════════

        if (Match(type: TokenType.Return))
        {
            return ParseReturnStatement();
        }

        if (Match(type: TokenType.Becomes))
        {
            return ParseBecomesStatement();
        }

        if (Match(type: TokenType.Break))
        {
            return ParseBreakStatement();
        }

        if (Match(type: TokenType.Continue))
        {
            return ParseContinueStatement();
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // SPECIAL STATEMENTS
        // ═══════════════════════════════════════════════════════════════════════════

        if (Match(type: TokenType.Pass))
        {
            return ParsePassStatement();
        }

        if (Match(type: TokenType.Throw))
        {
            return ParseThrowStatement(isFatal: false);
        }

        if (Match(type: TokenType.Pierce))
        {
            return ParseThrowStatement(isFatal: true);
        }

        // Using block (scoped resource management with indented body)
        if (Match(type: TokenType.Using))
        {
            return ParseUsingStatement();
        }

        if (Match(type: TokenType.Absent))
        {
            return ParseAbsentStatement();
        }

        if (Match(type: TokenType.Discard))
        {
            return ParseDiscardStatement();
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // RF-ONLY: MEMORY/SCOPE BLOCKS
        // ═══════════════════════════════════════════════════════════════════════════

        // Danger block (unsafe operations) - RazorForge only
        if (_language == Language.RazorForge && Match(type: TokenType.Danger))
        {
            return ParseDangerStatement();
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // DECLARATIONS IN STATEMENT CONTEXT
        // ═══════════════════════════════════════════════════════════════════════════

        // Variable declarations (can appear in statement context)
        bool stmtLateInit = false;
        if (Check(type: TokenType.LateInit) && PeekToken(offset: 1).Type == TokenType.Var)
        {
            Advance(); // consume 'lateinit'
            stmtLateInit = true;
        }
        if (Match(TokenType.Var, TokenType.Preset))
        {
            // Check if this is destructuring: var (a, b) = expr
            if (Check(type: TokenType.LeftParen))
            {
                return ParseDestructuringDeclaration();
            }

            VariableDeclaration varDecl = ParseVariableDeclaration(isLateInit: stmtLateInit);
            // Wrap the variable declaration as a declaration statement
            return new DeclarationStatement(Declaration: varDecl, Location: varDecl.Location);
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // EXPRESSION STATEMENT (FALLBACK)
        // ═══════════════════════════════════════════════════════════════════════════

        return ParseExpressionStatement();
    }
}
