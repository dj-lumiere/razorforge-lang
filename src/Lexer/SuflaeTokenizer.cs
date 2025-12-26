namespace Compilers.Suflae.Lexer;

using Compilers.Shared.Lexer;

/// <summary>
/// Tokenizer implementation for the Suflae programming language.
/// </summary>
/// <remarks>
/// <para>
/// Suflae is a Python-inspired syntax variant of RazorForge that uses significant
/// indentation instead of braces for block structure. Key differences from RazorForge:
/// </para>
/// <list type="bullet">
///   <item><description>Indentation-based blocks (4 spaces per level)</description></item>
///   <item><description>No braces { } - colons followed by newlines start blocks</description></item>
///   <item><description>Simplified numeric types (no 8-bit or 16-bit suffixes)</description></item>
///   <item><description>Character literals use byte' and letter' prefixes</description></item>
///   <item><description>Text literals use b"..." prefix for bytes</description></item>
///   <item><description>Significant newlines (statement terminators)</description></item>
///   <item><description>Script mode detection for top-level code</description></item>
/// </list>
/// <para>
/// The tokenizer produces INDENT and DEDENT tokens to represent block structure,
/// similar to Python's tokenization model.
/// </para>
/// </remarks>
public partial class SuflaeTokenizer
{
    #region Fields

    /// <summary>
    /// The complete source code text being tokenized.
    /// </summary>
    private readonly string _source;

    /// <summary>
    /// Current character position in the source text (0-based index).
    /// </summary>
    private int _position;

    /// <summary>
    /// Current line number in the source text (1-based).
    /// </summary>
    private int _line = 1;

    /// <summary>
    /// Current column number in the current line (1-based).
    /// </summary>
    private int _column = 1;

    /// <summary>
    /// Starting position of the current token being processed.
    /// </summary>
    private int _tokenStart;

    /// <summary>
    /// Starting column of the current token being processed.
    /// </summary>
    private int _tokenStartColumn;

    /// <summary>
    /// Starting line of the current token being processed.
    /// </summary>
    private int _tokenStartLine;

    /// <summary>
    /// List of tokens that have been successfully parsed from the source.
    /// </summary>
    private readonly List<Token> _tokens = [];

    /// <summary>
    /// Current indentation level (measured in units of 4 spaces).
    /// </summary>
    /// <remarks>
    /// An indentation level of 0 means no indentation (column 1).
    /// Level 1 = 4 spaces, Level 2 = 8 spaces, etc.
    /// </remarks>
    private int _currentIndentLevel;

    /// <summary>
    /// Flag indicating that an INDENT token is expected on the next non-empty line.
    /// </summary>
    /// <remarks>
    /// Set to true after encountering a colon that ends a line (block starter).
    /// The next line must have greater indentation than the current level.
    /// </remarks>
    private bool _expectIndent;

    /// <summary>
    /// Flag tracking whether any non-whitespace tokens have been processed on the current line.
    /// </summary>
    /// <remarks>
    /// Used to determine whether newlines are significant. A newline after
    /// actual content is significant; a blank line is not.
    /// </remarks>
    private bool _hasTokenOnLine;

    /// <summary>
    /// Flag tracking whether any definitions (routine, entity, etc.) have been found.
    /// </summary>
    /// <remarks>
    /// Used to detect script mode - if no definitions are found, the file is
    /// treated as a script with implicit main routine wrapping.
    /// </remarks>
    private bool _hasDefinitions;

    #endregion

    #region Keywords

    /// <summary>
    /// Dictionary mapping Suflae keywords to their corresponding token types.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Suflae shares most keywords with RazorForge, but excludes some
    /// RazorForge-specific keywords like 'danger' (Suflae doesn't support danger blocks).
    /// </para>
    /// </remarks>
    private readonly Dictionary<string, TokenType> _keywords = new()
    {
        [key: "routine"] = TokenType.Routine,
        [key: "entity"] = TokenType.Entity,
        [key: "record"] = TokenType.Record,
        [key: "variant"] = TokenType.Variant,
        [key: "choice"] = TokenType.Choice,
        [key: "requires"] = TokenType.Requires,
        [key: "protocol"] = TokenType.Protocol,
        [key: "let"] = TokenType.Let,
        [key: "var"] = TokenType.Var,
        [key: "preset"] = TokenType.Preset,
        [key: "common"] = TokenType.Common,
        [key: "private"] = TokenType.Private,
        [key: "internal"] = TokenType.Internal,
        [key: "public"] = TokenType.Public,
        [key: "global"] = TokenType.Global,
        [key: "imported"] = TokenType.Imported,
        [key: "me"] = TokenType.Me,
        [key: "MyType"] = TokenType.MyType,
        [key: "if"] = TokenType.If,
        [key: "elseif"] = TokenType.Elseif,
        [key: "else"] = TokenType.Else,
        [key: "then"] = TokenType.Then,
        [key: "unless"] = TokenType.Unless,
        [key: "break"] = TokenType.Break,
        [key: "continue"] = TokenType.Continue,
        [key: "return"] = TokenType.Return,
        [key: "throw"] = TokenType.Throw,
        [key: "absent"] = TokenType.Absent,
        [key: "for"] = TokenType.For,
        [key: "loop"] = TokenType.Loop,
        [key: "while"] = TokenType.While,
        [key: "when"] = TokenType.When,
        [key: "is"] = TokenType.Is,
        [key: "follows"] = TokenType.Follows,
        [key: "import"] = TokenType.Import,
        [key: "namespace"] = TokenType.Namespace,
        [key: "define"] = TokenType.Define,
        [key: "using"] = TokenType.Using,
        [key: "as"] = TokenType.As,
        [key: "pass"] = TokenType.Pass,
        [key: "with"] = TokenType.With,
        [key: "isnot"] = TokenType.IsNot,
        [key: "notin"] = TokenType.NotIn,
        [key: "notfollows"] = TokenType.NotFollows,
        [key: "in"] = TokenType.In,
        [key: "to"] = TokenType.To,
        [key: "downto"] = TokenType.Downto,
        [key: "by"] = TokenType.By,
        [key: "and"] = TokenType.And,
        [key: "or"] = TokenType.Or,
        [key: "not"] = TokenType.Not,
        [key: "true"] = TokenType.True,
        [key: "false"] = TokenType.False,
        [key: "none"] = TokenType.None,
        [key: "generate"] = TokenType.Generate,
        [key: "suspended"] = TokenType.Suspended,
        [key: "waitfor"] = TokenType.Waitfor
    };

    #endregion

    #region Suffix Mappings

    /// <summary>
    /// Maps numeric type suffixes to their corresponding token types.
    /// </summary>
    /// <remarks>
    /// Suflae has a simplified numeric type system compared to RazorForge:
    /// only 32-bit and larger types are supported directly.
    /// </remarks>
    private readonly Dictionary<string, TokenType> _numericSuffixToTokenType = new()
    {
        [key: "s32"] = TokenType.S32Literal,
        [key: "s64"] = TokenType.S64Literal,
        [key: "s128"] = TokenType.S128Literal,
        [key: "saddr"] = TokenType.SaddrLiteral,
        [key: "u32"] = TokenType.U32Literal,
        [key: "u64"] = TokenType.U64Literal,
        [key: "u128"] = TokenType.U128Literal,
        [key: "uaddr"] = TokenType.UaddrLiteral,
        [key: "f32"] = TokenType.F32Literal,
        [key: "f64"] = TokenType.F64Literal,
        [key: "d128"] = TokenType.D128Literal
    };

    /// <summary>
    /// Maps memory size suffixes to their corresponding token types.
    /// </summary>
    private readonly Dictionary<string, TokenType> _memorySuffixToTokenType = new()
    {
        [key: "b"] = TokenType.ByteLiteral,
        [key: "kb"] = TokenType.KilobyteLiteral,
        [key: "kib"] = TokenType.KibibyteLiteral,
        [key: "mb"] = TokenType.MegabyteLiteral,
        [key: "mib"] = TokenType.MebibyteLiteral,
        [key: "gb"] = TokenType.GigabyteLiteral,
        [key: "gib"] = TokenType.GibibyteLiteral,
    };

    /// <summary>
    /// Maps duration suffixes to their corresponding token types.
    /// </summary>
    private readonly Dictionary<string, TokenType> _durationSuffixToTokenType = new()
    {
        [key: "w"] = TokenType.WeekLiteral,
        [key: "d"] = TokenType.DayLiteral,
        [key: "h"] = TokenType.HourLiteral,
        [key: "m"] = TokenType.MinuteLiteral,
        [key: "s"] = TokenType.SecondLiteral,
        [key: "ms"] = TokenType.MillisecondLiteral,
        [key: "us"] = TokenType.MicrosecondLiteral,
        [key: "ns"] = TokenType.NanosecondLiteral
    };

    /// <summary>
    /// Maps text literal prefixes to their corresponding token types.
    /// </summary>
    /// <remarks>
    /// Suflae uses a simplified prefix system: r, f, rf for text, and b, br, bf, brf for bytes.
    /// </remarks>
    private readonly Dictionary<string, TokenType> _textPrefixToTokenType = new()
    {
        [key: "r"] = TokenType.RawText,
        [key: "f"] = TokenType.FormattedText,
        [key: "rf"] = TokenType.RawFormattedText,
        [key: "b"] = TokenType.BytesLiteral,
        [key: "br"] = TokenType.BytesRawLiteral,
        [key: "bf"] = TokenType.BytesFormatted,
        [key: "brf"] = TokenType.BytesRawFormatted
    };

    /// <summary>
    /// List of all valid text prefixes for prefix matching.
    /// </summary>
    private readonly List<string> _textPrefixes =
    [
        "r", "f", "rf", "b", "br", "bf", "brf"
    ];

    #endregion

    #region Constructor and Properties

    /// <summary>
    /// Initializes a new instance of the <see cref="SuflaeTokenizer"/> class.
    /// </summary>
    /// <param name="source">The Suflae source code to tokenize.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="source"/> is null.</exception>
    public SuflaeTokenizer(string source)
    {
        _source = source ?? throw new ArgumentNullException(paramName: nameof(source));
    }

    /// <summary>
    /// Gets a value indicating whether the source code is in script mode.
    /// </summary>
    /// <value>
    /// <c>true</c> if no definitions (routine, entity, record, etc.) were found;
    /// <c>false</c> if at least one definition was found.
    /// </value>
    /// <remarks>
    /// <para>
    /// Script mode allows Suflae files to contain top-level statements without
    /// an explicit main routine. The compiler wraps script mode code in an
    /// implicit main routine.
    /// </para>
    /// <para>
    /// This property is only valid after <see cref="Tokenize"/> has been called.
    /// </para>
    /// </remarks>
    public bool IsScriptMode => !_hasDefinitions;

    #endregion

    #region Public Methods

    /// <summary>
    /// Tokenizes the entire source code and returns a list of tokens.
    /// </summary>
    /// <returns>
    /// A list of <see cref="Token"/> objects representing the tokenized source code.
    /// The list includes INDENT/DEDENT tokens for block structure and ends with EOF.
    /// </returns>
    /// <exception cref="LexerException">
    /// Thrown when the source contains invalid syntax, such as misaligned indentation,
    /// unterminated strings, or invalid escape sequences.
    /// </exception>
    /// <remarks>
    /// <para>
    /// After tokenization completes, any remaining indentation levels are closed
    /// with DEDENT tokens to ensure proper block closure.
    /// </para>
    /// <para>
    /// After calling this method, the <see cref="IsScriptMode"/> property reflects
    /// whether the source contained any definition keywords.
    /// </para>
    /// </remarks>
    public List<Token> Tokenize()
    {
        while (!IsAtEnd())
        {
            _tokenStart = _position;
            _tokenStartColumn = _column;
            _tokenStartLine = _line;

            ScanToken();
        }

        // Emit remaining DEDENT tokens at end of file
        for (int i = 0; i < _currentIndentLevel; i += 1)
        {
            AddToken(type: TokenType.Dedent, text: "");
        }

        _tokens.Add(item: new Token(Type: TokenType.Eof,
            Text: "",
            Line: _line,
            Column: _column,
            Position: _position));
        return _tokens;
    }

    #endregion
}
