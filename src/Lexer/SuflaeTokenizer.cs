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
        ["routine"] = TokenType.Routine,
        ["entity"] = TokenType.Entity,
        ["record"] = TokenType.Record,
        ["variant"] = TokenType.Variant,
        ["choice"] = TokenType.Choice,
        ["requires"] = TokenType.Requires,
        ["protocol"] = TokenType.Protocol,
        ["let"] = TokenType.Let,
        ["var"] = TokenType.Var,
        ["preset"] = TokenType.Preset,
        ["common"] = TokenType.TypeWise,
        ["private"] = TokenType.Private,
        ["family"] = TokenType.Family,
        ["internal"] = TokenType.Internal,
        ["public"] = TokenType.Public,
        ["global"] = TokenType.Global,
        ["imported"] = TokenType.Imported,
        ["me"] = TokenType.Me,
        ["MyType"] = TokenType.MyType,
        ["parent"] = TokenType.Parent,
        ["if"] = TokenType.If,
        ["elseif"] = TokenType.Elseif,
        ["else"] = TokenType.Else,
        ["then"] = TokenType.Then,
        ["unless"] = TokenType.Unless,
        ["break"] = TokenType.Break,
        ["continue"] = TokenType.Continue,
        ["return"] = TokenType.Return,
        ["throw"] = TokenType.Throw,
        ["absent"] = TokenType.Absent,
        ["for"] = TokenType.For,
        ["loop"] = TokenType.Loop,
        ["while"] = TokenType.While,
        ["when"] = TokenType.When,
        ["is"] = TokenType.Is,
        ["from"] = TokenType.From,
        ["follows"] = TokenType.Follows,
        ["import"] = TokenType.Import,
        ["namespace"] = TokenType.Namespace,
        ["define"] = TokenType.Define,
        ["using"] = TokenType.Using,
        ["as"] = TokenType.As,
        ["pass"] = TokenType.Pass,
        ["with"] = TokenType.With,
        ["where"] = TokenType.Where,
        ["isnot"] = TokenType.IsNot,
        ["notfrom"] = TokenType.NotFrom,
        ["notin"] = TokenType.NotIn,
        ["notfollows"] = TokenType.NotFollows,
        ["in"] = TokenType.In,
        ["to"] = TokenType.To,
        ["downto"] = TokenType.Downto,
        ["by"] = TokenType.By,
        ["and"] = TokenType.And,
        ["or"] = TokenType.Or,
        ["not"] = TokenType.Not,
        ["true"] = TokenType.True,
        ["false"] = TokenType.False,
        ["none"] = TokenType.None,
        ["generate"] = TokenType.Generate,
        ["suspended"] = TokenType.Suspended,
        ["waitfor"] = TokenType.Waitfor
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
        ["s32"] = TokenType.S32Literal,
        ["s64"] = TokenType.S64Literal,
        ["s128"] = TokenType.S128Literal,
        ["saddr"] = TokenType.SaddrLiteral,
        ["u32"] = TokenType.U32Literal,
        ["u64"] = TokenType.U64Literal,
        ["u128"] = TokenType.U128Literal,
        ["uaddr"] = TokenType.UaddrLiteral,
        ["f32"] = TokenType.F32Literal,
        ["f64"] = TokenType.F64Literal,
        ["d128"] = TokenType.D128Literal
    };

    /// <summary>
    /// Maps memory size suffixes to their corresponding token types.
    /// </summary>
    private readonly Dictionary<string, TokenType> _memorySuffixToTokenType = new()
    {
        ["b"] = TokenType.ByteLiteral,
        ["kb"] = TokenType.KilobyteLiteral,
        ["kib"] = TokenType.KibibyteLiteral,
        ["kbit"] = TokenType.KilobitLiteral,
        ["kibit"] = TokenType.KibibitLiteral,
        ["mb"] = TokenType.MegabyteLiteral,
        ["mib"] = TokenType.MebibyteLiteral,
        ["mbit"] = TokenType.MegabitLiteral,
        ["mibit"] = TokenType.MebibitLiteral,
        ["gb"] = TokenType.GigabyteLiteral,
        ["gib"] = TokenType.GibibyteLiteral,
        ["gbit"] = TokenType.GigabitLiteral,
        ["gibit"] = TokenType.GibibitLiteral,
        ["tb"] = TokenType.TerabyteLiteral,
        ["tib"] = TokenType.TebibyteLiteral,
        ["tbit"] = TokenType.TerabitLiteral,
        ["tibit"] = TokenType.TebibitLiteral,
        ["pb"] = TokenType.PetabyteLiteral,
        ["pib"] = TokenType.PebibyteLiteral,
        ["pbit"] = TokenType.PetabitLiteral,
        ["pibit"] = TokenType.PebibitLiteral
    };

    /// <summary>
    /// Maps duration suffixes to their corresponding token types.
    /// </summary>
    private readonly Dictionary<string, TokenType> _durationSuffixToTokenType = new()
    {
        ["w"] = TokenType.WeekLiteral,
        ["d"] = TokenType.DayLiteral,
        ["h"] = TokenType.HourLiteral,
        ["m"] = TokenType.MinuteLiteral,
        ["s"] = TokenType.SecondLiteral,
        ["ms"] = TokenType.MillisecondLiteral,
        ["us"] = TokenType.MicrosecondLiteral,
        ["ns"] = TokenType.NanosecondLiteral
    };

    /// <summary>
    /// Maps text literal prefixes to their corresponding token types.
    /// </summary>
    /// <remarks>
    /// Suflae uses a simplified prefix system: r, f, rf for text, and b, br, bf, brf for bytes.
    /// </remarks>
    private readonly Dictionary<string, TokenType> _textPrefixToTokenType = new()
    {
        ["r"] = TokenType.RawText,
        ["f"] = TokenType.FormattedText,
        ["rf"] = TokenType.RawFormattedText,
        ["b"] = TokenType.BytesLiteral,
        ["br"] = TokenType.BytesRawLiteral,
        ["bf"] = TokenType.BytesFormatted,
        ["brf"] = TokenType.BytesRawFormatted
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
        _source = source ?? throw new ArgumentNullException(nameof(source));
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
            AddToken(TokenType.Dedent, "");
        }

        _tokens.Add(new Token(
            Type: TokenType.Eof,
            Text: "",
            Line: _line,
            Column: _column,
            Position: _position));
        return _tokens;
    }

    #endregion
}
