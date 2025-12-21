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
    ///   <item><description>Access modifiers: private, internal, public, global</description></item>
    ///   <item><description>Control flow: if, elseif, else, when, for, loop, while, break, continue, return</description></item>
    ///   <item><description>Type operations: is, isnot, from, notfrom, follows, notfollows, in, notin</description></item>
    ///   <item><description>Memory management: viewing, hijacking, seizing, inspecting</description></item>
    ///   <item><description>Literals: true, false, none, absent</description></item>
    /// </list>
    /// </para>
    /// </remarks>
    private readonly Dictionary<string, TokenType> _keywords = new()
    {
        [key: "routine"] = TokenType.Routine,
        [key: "resident"] = TokenType.Resident,
        [key: "choice"] = TokenType.Choice,
        [key: "variant"] = TokenType.Variant,
        [key: "mutant"] = TokenType.Mutant,
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
        [key: "danger"] = TokenType.Danger,
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
        [key: "entity"] = TokenType.Entity,
        [key: "record"] = TokenType.Record,
        [key: "protocol"] = TokenType.Protocol,
        [key: "requires"] = TokenType.Requires,
        [key: "generate"] = TokenType.Generate,
        [key: "suspended"] = TokenType.Suspended,
        [key: "waitfor"] = TokenType.Waitfor,
        [key: "viewing"] = TokenType.Viewing,
        [key: "hijacking"] = TokenType.Hijacking,
        [key: "seizing"] = TokenType.Seizing,
        [key: "inspecting"] = TokenType.Inspecting
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
        [key: "s8"] = TokenType.S8Literal,
        [key: "s16"] = TokenType.S16Literal,
        [key: "s32"] = TokenType.S32Literal,
        [key: "s64"] = TokenType.S64Literal,
        [key: "s128"] = TokenType.S128Literal,
        [key: "saddr"] = TokenType.SaddrLiteral,
        [key: "u8"] = TokenType.U8Literal,
        [key: "u16"] = TokenType.U16Literal,
        [key: "u32"] = TokenType.U32Literal,
        [key: "u64"] = TokenType.U64Literal,
        [key: "u128"] = TokenType.U128Literal,
        [key: "uaddr"] = TokenType.UaddrLiteral,
        [key: "f16"] = TokenType.F16Literal,
        [key: "f32"] = TokenType.F32Literal,
        [key: "f64"] = TokenType.F64Literal,
        [key: "f128"] = TokenType.F128Literal,
        [key: "d32"] = TokenType.D32Literal,
        [key: "d64"] = TokenType.D64Literal,
        [key: "d128"] = TokenType.D128Literal
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
        [key: "b"] = TokenType.ByteLiteral,
        [key: "kb"] = TokenType.KilobyteLiteral,
        [key: "kib"] = TokenType.KibibyteLiteral,
        [key: "kbit"] = TokenType.KilobitLiteral,
        [key: "kibit"] = TokenType.KibibitLiteral,
        [key: "mb"] = TokenType.MegabyteLiteral,
        [key: "mib"] = TokenType.MebibyteLiteral,
        [key: "mbit"] = TokenType.MegabitLiteral,
        [key: "mibit"] = TokenType.MebibitLiteral,
        [key: "gb"] = TokenType.GigabyteLiteral,
        [key: "gib"] = TokenType.GibibyteLiteral,
        [key: "gbit"] = TokenType.GigabitLiteral,
        [key: "gibit"] = TokenType.GibibitLiteral,
        [key: "tb"] = TokenType.TerabyteLiteral,
        [key: "tib"] = TokenType.TebibyteLiteral,
        [key: "tbit"] = TokenType.TerabitLiteral,
        [key: "tibit"] = TokenType.TebibitLiteral,
        [key: "pb"] = TokenType.PetabyteLiteral,
        [key: "pib"] = TokenType.PebibyteLiteral,
        [key: "pbit"] = TokenType.PetabitLiteral,
        [key: "pibit"] = TokenType.PebibitLiteral
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
        [key: "r"] = TokenType.RawText,
        [key: "f"] = TokenType.FormattedText,
        [key: "rf"] = TokenType.RawFormattedText,
        [key: "t8"] = TokenType.Text8Literal,
        [key: "t8r"] = TokenType.Text8RawText,
        [key: "t8f"] = TokenType.Text8FormattedText,
        [key: "t8rf"] = TokenType.Text8RawFormattedText,
        [key: "t16"] = TokenType.Text16Literal,
        [key: "t16r"] = TokenType.Text16RawText,
        [key: "t16f"] = TokenType.Text16FormattedText,
        [key: "t16rf"] = TokenType.Text16RawFormattedText
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
        _source = source ?? throw new ArgumentNullException(paramName: nameof(source));
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

        _tokens.Add(item: new Token(Type: TokenType.Eof,
            Text: "",
            Line: _line,
            Column: _column,
            Position: _position));
        return _tokens;
    }

    #endregion
}
