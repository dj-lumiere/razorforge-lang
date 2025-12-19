namespace Compilers.RazorForge.Lexer;

using Compilers.Shared.Lexer;

/// <summary>
/// Tokenizer implementation for the RazorForge programming language.
/// </summary>
/// <remarks>
/// <para>
/// RazorForge uses a C-style syntax with braced blocks, semicolons as optional statement
/// terminators, and a rich set of operators including overflow-checked variants.
/// </para>
/// <para>
/// This tokenizer handles:
/// <list type="bullet">
///   <item><description>Keywords and identifiers (snake_case vs PascalCase detection)</description></item>
///   <item><description>Numeric literals with type suffixes (s32, u64, f32, etc.)</description></item>
///   <item><description>Text literals with prefixes (r"raw", f"formatted", t8"utf8", t16"utf16")</description></item>
///   <item><description>Character literals with bit-width prefixes (letter8, letter16, letter32)</description></item>
///   <item><description>Overflow operators (+%, -^, *?, etc.)</description></item>
///   <item><description>Memory size literals (1kb, 2mib, etc.)</description></item>
///   <item><description>Duration literals (1s, 500ms, 2h, etc.)</description></item>
///   <item><description>Documentation comments (###)</description></item>
/// </list>
/// </para>
/// </remarks>
public partial class RazorForgeTokenizer
{
    #region Fields

    /// <summary>
    /// The complete source code text being tokenized.
    /// </summary>
    /// <remarks>
    /// This field is immutable after construction and represents the entire
    /// input that will be processed by the tokenizer.
    /// </remarks>
    private readonly string _source;

    /// <summary>
    /// Current character position in the source text (0-based index).
    /// </summary>
    /// <remarks>
    /// This position advances as characters are consumed during tokenization.
    /// It represents the next character to be read.
    /// </remarks>
    private int _position;

    /// <summary>
    /// Current line number in the source text (1-based).
    /// </summary>
    /// <remarks>
    /// Line numbers start at 1 and increment each time a newline character is consumed.
    /// Used for error reporting and token location tracking.
    /// </remarks>
    private int _line = 1;

    /// <summary>
    /// Current column number in the current line (1-based).
    /// </summary>
    /// <remarks>
    /// Column numbers start at 1 at the beginning of each line and increment
    /// with each character consumed. Reset to 1 after each newline.
    /// </remarks>
    private int _column = 1;

    /// <summary>
    /// Starting position of the current token being processed.
    /// </summary>
    /// <remarks>
    /// Captured at the beginning of each token scan to enable extraction
    /// of the token's text from the source.
    /// </remarks>
    private int _tokenStart;

    /// <summary>
    /// Starting column of the current token being processed.
    /// </summary>
    /// <remarks>
    /// Captured at the beginning of each token scan for accurate
    /// location reporting in the resulting token.
    /// </remarks>
    private int _tokenStartColumn;

    /// <summary>
    /// Starting line of the current token being processed.
    /// </summary>
    /// <remarks>
    /// Captured at the beginning of each token scan for accurate
    /// location reporting in the resulting token.
    /// </remarks>
    private int _tokenStartLine;

    /// <summary>
    /// List of tokens that have been successfully parsed from the source.
    /// </summary>
    /// <remarks>
    /// Tokens are appended to this list as they are recognized during scanning.
    /// The final list includes an EOF token at the end.
    /// </remarks>
    private readonly List<Token> _tokens = [];

    #endregion

    #region Keywords

    /// <summary>
    /// Dictionary mapping RazorForge keywords to their corresponding token types.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This dictionary is used during identifier scanning to distinguish between
    /// keywords and regular identifiers. Keywords take precedence.
    /// </para>
    /// <para>
    /// Categories of keywords include:
    /// <list type="bullet">
    ///   <item><description>Declaration keywords: routine, entity, record, choice, variant, protocol</description></item>
    ///   <item><description>Variable keywords: let, var, preset</description></item>
    ///   <item><description>Access modifiers: private, family, internal, public, global</description></item>
    ///   <item><description>Control flow: if, elseif, else, when, for, loop, while, break, continue, return</description></item>
    ///   <item><description>Type operations: is, isnot, from, notfrom, follows, notfollows, in, notin</description></item>
    ///   <item><description>Memory management: usurping, viewing, hijacking, seizing, inspecting</description></item>
    ///   <item><description>Literals: true, false, none, absent</description></item>
    /// </list>
    /// </para>
    /// </remarks>
    private readonly Dictionary<string, TokenType> _keywords = new()
    {
        ["routine"] = TokenType.Routine,
        ["resident"] = TokenType.Resident,
        ["choice"] = TokenType.Choice,
        ["variant"] = TokenType.Variant,
        ["mutant"] = TokenType.Mutant,
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
        ["danger"] = TokenType.Danger,
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
        ["entity"] = TokenType.Entity,
        ["record"] = TokenType.Record,
        ["protocol"] = TokenType.Protocol,
        ["requires"] = TokenType.Requires,
        ["generate"] = TokenType.Generate,
        ["suspended"] = TokenType.Suspended,
        ["waitfor"] = TokenType.Waitfor,
        ["usurping"] = TokenType.Usurping,
        ["viewing"] = TokenType.Viewing,
        ["hijacking"] = TokenType.Hijacking,
        ["seizing"] = TokenType.Seizing,
        ["inspecting"] = TokenType.Inspecting
    };

    #endregion

    #region Suffix Mappings

    /// <summary>
    /// Maps numeric type suffixes to their corresponding token types.
    /// </summary>
    /// <remarks>
    /// Supports signed integers (s8-s128, saddr), unsigned integers (u8-u128, uaddr),
    /// floating point (f16-f128), and decimal floating point (d32-d128).
    /// </remarks>
    private readonly Dictionary<string, TokenType> _numericSuffixToTokenType = new()
    {
        ["s8"] = TokenType.S8Literal,
        ["s16"] = TokenType.S16Literal,
        ["s32"] = TokenType.S32Literal,
        ["s64"] = TokenType.S64Literal,
        ["s128"] = TokenType.S128Literal,
        ["saddr"] = TokenType.SaddrLiteral,
        ["u8"] = TokenType.U8Literal,
        ["u16"] = TokenType.U16Literal,
        ["u32"] = TokenType.U32Literal,
        ["u64"] = TokenType.U64Literal,
        ["u128"] = TokenType.U128Literal,
        ["uaddr"] = TokenType.UaddrLiteral,
        ["f16"] = TokenType.F16Literal,
        ["f32"] = TokenType.F32Literal,
        ["f64"] = TokenType.F64Literal,
        ["f128"] = TokenType.F128Literal,
        ["d32"] = TokenType.D32Literal,
        ["d64"] = TokenType.D64Literal,
        ["d128"] = TokenType.D128Literal
    };

    /// <summary>
    /// Maps memory size suffixes to their corresponding token types.
    /// </summary>
    /// <remarks>
    /// Supports both SI units (kb, mb, gb, tb, pb) and binary units (kib, mib, gib, tib, pib),
    /// as well as bit-based units (kbit, mbit, etc.).
    /// </remarks>
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
    /// <remarks>
    /// Supports weeks (w), days (d), hours (h), minutes (m), seconds (s),
    /// milliseconds (ms), microseconds (us), and nanoseconds (ns).
    /// </remarks>
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
    /// <para>Supported prefixes:</para>
    /// <list type="bullet">
    ///   <item><description>r - Raw text (no escape processing)</description></item>
    ///   <item><description>f - Formatted text (interpolation)</description></item>
    ///   <item><description>rf - Raw formatted text</description></item>
    ///   <item><description>t8 - UTF-8 text</description></item>
    ///   <item><description>t16 - UTF-16 text</description></item>
    ///   <item><description>Combinations: t8r, t8f, t8rf, t16r, t16f, t16rf</description></item>
    /// </list>
    /// </remarks>
    private readonly Dictionary<string, TokenType> _textPrefixToTokenType = new()
    {
        ["r"] = TokenType.RawText,
        ["f"] = TokenType.FormattedText,
        ["rf"] = TokenType.RawFormattedText,
        ["t8"] = TokenType.Text8Literal,
        ["t8r"] = TokenType.Text8RawText,
        ["t8f"] = TokenType.Text8FormattedText,
        ["t8rf"] = TokenType.Text8RawFormattedText,
        ["t16"] = TokenType.Text16Literal,
        ["t16r"] = TokenType.Text16RawText,
        ["t16f"] = TokenType.Text16FormattedText,
        ["t16rf"] = TokenType.Text16RawFormattedText
    };

    /// <summary>
    /// List of all valid text prefixes for prefix matching during tokenization.
    /// </summary>
    /// <remarks>
    /// Used for greedy matching of text prefixes - the tokenizer tries to match
    /// the longest valid prefix first.
    /// </remarks>
    private readonly List<string> _textPrefixes =
    [
        "r", "f", "rf", "t8", "t8r", "t8f", "t8rf", "t16", "t16r", "t16f", "t16rf"
    ];

    #endregion

    #region Constructor

    /// <summary>
    /// Initializes a new instance of the <see cref="RazorForgeTokenizer"/> class.
    /// </summary>
    /// <param name="source">The RazorForge source code to tokenize.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="source"/> is null.</exception>
    public RazorForgeTokenizer(string source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
    }

    #endregion

    #region Public Methods

    /// <summary>
    /// Tokenizes the entire source code and returns a list of tokens.
    /// </summary>
    /// <returns>
    /// A list of <see cref="Token"/> objects representing the tokenized source code.
    /// The list always ends with an EOF token.
    /// </returns>
    /// <exception cref="LexerException">
    /// Thrown when the source contains invalid syntax that cannot be tokenized,
    /// such as unterminated strings, invalid escape sequences, or unknown suffixes.
    /// </exception>
    /// <remarks>
    /// <para>
    /// This method processes the entire source code from start to finish,
    /// producing a complete token stream. Each call to this method starts
    /// fresh from the beginning of the source.
    /// </para>
    /// <para>
    /// Whitespace (spaces, tabs, carriage returns, and newlines) is consumed
    /// but not included in the output token stream, except that semicolons
    /// produce Newline tokens to support optional statement terminators.
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
