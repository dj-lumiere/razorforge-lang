using System;
using System.Collections.Generic;
using Compiler.Diagnostics;
using System.Globalization;
using TypeModel.Enums;

namespace Compiler.Tokenizer;

/// <summary>
/// Unified tokenizer for both RazorForge and Suflae programming languages.
/// </summary>
/// <remarks>
/// <para>
/// This tokenizer handles both language variants through a shared keyword map with
/// language-conditional entries. Both languages use significant indentation and
/// newlines for block structure. Suffix mappings for numeric,
/// bytesize, and duration literals are shared between both languages.
/// </para>
/// </remarks>
public partial class Tokenizer
{
    #region Fields

    private readonly string _source;
    private readonly string _fileName;
    private readonly Language _language;
    private int _position;
    private int _line = 1;
    private int _column = 1;
    private int _tokenStart;
    private int _tokenStartColumn;
    private int _tokenStartLine;
    private readonly List<Token> _tokens = [];
    private readonly Stack<int> _indentStack = new([0]);
    private bool _hasTokenOnLine;
    private int _bracketDepth;
    private bool _hasDefinitions;

    #endregion

    #region Keywords

    /// <summary>
    /// Dictionary mapping keywords to their corresponding token types.
    /// Guarded keywords are always present; RF-only keywords are added conditionally.
    /// </summary>
    private readonly Dictionary<string, TokenType> _keywords;

    #endregion

    #region Suffix Mappings

    /// <summary>
    /// Maps numeric type suffixes to their corresponding token types.
    /// </summary>
    private readonly Dictionary<string, TokenType> _numericSuffixToTokenType;

    /// <summary>
    /// The suffix for arbitrary-precision Integer literals (e.g. `42n`).
    /// </summary>
    private const string ArbitraryIntegerSuffix = "n";

    /// <summary>
    /// The suffix for arbitrary-precision Decimal literals (e.g. `3.14dn`).
    /// Mirrors the fixed-width Decimal suffix family `d32`/`d64`/`d128`.
    /// </summary>
    private const string ArbitraryDecimalSuffix = "dn";

    /// <summary>
    /// Maps ByteSize suffixes to their corresponding token types.
    /// </summary>
    private readonly Dictionary<string, TokenType> _byteSizeSuffixToTokenType = new()
    {
        [key: "b"] = TokenType.ByteLiteral,
        [key: "kb"] = TokenType.KilobyteLiteral,
        [key: "kib"] = TokenType.KibibyteLiteral,
        [key: "mb"] = TokenType.MegabyteLiteral,
        [key: "mib"] = TokenType.MebibyteLiteral,
        [key: "gb"] = TokenType.GigabyteLiteral,
        [key: "gib"] = TokenType.GibibyteLiteral
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
    private readonly Dictionary<string, TokenType> _textPrefixToTokenType = new()
    {
        [key: "r"] = TokenType.RawText,
        [key: "f"] = TokenType.FormattedText,
        [key: "rf"] = TokenType.RawFormattedText,
        [key: "b"] = TokenType.BytesLiteral,
        [key: "br"] = TokenType.BytesRawLiteral
    };

    /// <summary>
    /// List of all valid text prefixes for prefix matching.
    /// </summary>
    private readonly List<string> _textPrefixes =
    [
        "r", "f", "rf", "b", "br"
    ];

    #endregion

    #region Constructor and Properties

    /// <summary>
    /// Initializes a new instance of the <see cref="Tokenizer"/> class.
    /// </summary>
    /// <param name="source">The source code to tokenize.</param>
    /// <param name="fileName">The source code's file name being tokenized.</param>
    /// <param name="language">The language variant (RazorForge or Suflae).</param>
    public Tokenizer(string source, string fileName, Language language)
    {
        _fileName = fileName;
        _source = NormalizeAndValidateSource(source: source ??
            throw new ArgumentNullException(paramName: nameof(source)),
            fileName: fileName,
            language: language);
        _language = language;

        // Guarded keywords (both RF and SF)
        _keywords = new Dictionary<string, TokenType>
        {
            // Type declarations
            [key: "routine"] = TokenType.Routine,
            [key: "entity"] = TokenType.Entity,
            [key: "record"] = TokenType.Record,
            [key: "choice"] = TokenType.Choice,
            [key: "flags"] = TokenType.Flags,
            [key: "crashable"] = TokenType.Crashable,
            [key: "variant"] = TokenType.Variant,
            [key: "protocol"] = TokenType.Protocol,

            // Variable declarations
            [key: "var"] = TokenType.Var,
            [key: "preset"] = TokenType.Preset,
            [key: "global"] = TokenType.Global,
            [key: "lateinit"] = TokenType.LateInit,

            // Access modifiers
            [key: "secret"] = TokenType.Secret,
            [key: "posted"] = TokenType.Posted,
            [key: "common"] = TokenType.Common,

            // Self references
            [key: "me"] = TokenType.Me,
            [key: "Me"] = TokenType.MyType,

            // Protocol implementation (renamed keywords, old TokenType names)
            [key: "obeys"] = TokenType.Obeys,
            [key: "disobeys"] = TokenType.Disobeys,

            // Control flow
            [key: "if"] = TokenType.If,
            [key: "elseif"] = TokenType.Elseif,
            [key: "else"] = TokenType.Else,
            [key: "then"] = TokenType.Then,
            [key: "unless"] = TokenType.Unless,
            [key: "when"] = TokenType.When,
            [key: "is"] = TokenType.Is,
            [key: "loop"] = TokenType.Loop,
            [key: "while"] = TokenType.While,
            [key: "each"] = TokenType.Each,
            [key: "break"] = TokenType.Break,
            [key: "continue"] = TokenType.Continue,
            [key: "return"] = TokenType.Return,
            [key: "throw"] = TokenType.Throw,
            [key: "pierce"] = TokenType.Pierce,
            [key: "absent"] = TokenType.Absent,
            [key: "becomes"] = TokenType.Becomes,

            // Module system
            [key: "import"] = TokenType.Import,
            [key: "module"] = TokenType.Module,

            // Special keywords
            [key: "using"] = TokenType.Using,
            [key: "as"] = TokenType.As,
            [key: "define"] = TokenType.Define,
            [key: "pass"] = TokenType.Pass,
            [key: "with"] = TokenType.With,
            [key: "given"] = TokenType.Given,
            [key: "in"] = TokenType.In,
            [key: "notin"] = TokenType.NotIn,
            [key: "isnot"] = TokenType.IsNot,
            [key: "to"] = TokenType.To,
            [key: "til"] = TokenType.Til,
            [key: "by"] = TokenType.By,
            [key: "discard"] = TokenType.Discard,

            // Logical operators
            [key: "and"] = TokenType.And,
            [key: "or"] = TokenType.Or,
            [key: "not"] = TokenType.Not,
            [key: "but"] = TokenType.But,

            // Literals
            [key: "true"] = TokenType.True,
            [key: "false"] = TokenType.False,
            [key: "None"] = TokenType.None,
            [key: "none"] = TokenType.NoneValue,

            // Generic constraints
            [key: "needs"] = TokenType.Needs,
            [key: "onlyif"] = TokenType.OnlyIf,

            // Associated types
            [key: "relates"] = TokenType.Relates,

            // Concurrency (SF has cooperative coroutines; `threaded` stays RF-only)
            [key: "suspended"] = TokenType.Suspended,

            // `needs P everywhere` — the structural-conferral gate of the protocol model (a `needs`-clause
            // modifier, NOT a reflection construct). It lives with `needs`/`obeys`/`protocol`, which SF
            // shares, so it is a SHARED keyword. The derive TEMPLATES it gates (e.g. `routine T.eq` with an
            // `expand` body) are RF stdlib that SF reuses wholesale; authoring such a template needs
            // `expand` (RF-only), but the gate itself and the built-in structural protocols apply to SF
            // types just as well.
            [key: "everywhere"] = TokenType.Everywhere,
        };

        // RF-only keywords
        if (_language == Language.RazorForge)
        {
            _keywords[key: "danger"] = TokenType.Danger;
            _keywords[key: "dangerous"] = TokenType.Dangerous;
            _keywords[key: "steal"] = TokenType.Steal;
            _keywords[key: "threaded"] = TokenType.Threaded;
            // Comptime reflection is RF's model (monomorph unroll). Suflae's reflection is the runtime
            // ObjectHacker (later); SF inherits the RF-mode-compiled stdlib derives via wholesale Core
            // reuse, so it never needs `expand` itself. `expand` is the ONLY reflection keyword; the sources
            // (`openmemvarof`/`allmemvarof`/`caseof`/`branchof`) and accessors (`nameof`/`typeof`/…)
            // tokenize as plain identifiers and are classified in SA (see ExpandSources / the
            // BuilderExpansion import gate), not here.
            _keywords[key: "expand"] = TokenType.Expand;
        }

        // Numeric suffix map - shared between both languages except "j" default
        _numericSuffixToTokenType = new Dictionary<string, TokenType>
        {
            // Signed integers
            [key: "s8"] = TokenType.S8Literal,
            [key: "s16"] = TokenType.S16Literal,
            [key: "s32"] = TokenType.S32Literal,
            [key: "s64"] = TokenType.S64Literal,
            [key: "s128"] = TokenType.S128Literal,
            [key: "s256"] = TokenType.S256Literal,
            // Unsigned integers
            [key: "u8"] = TokenType.U8Literal,
            [key: "u16"] = TokenType.U16Literal,
            [key: "u32"] = TokenType.U32Literal,
            [key: "u64"] = TokenType.U64Literal,
            [key: "u128"] = TokenType.U128Literal,
            [key: "u256"] = TokenType.U256Literal,
            [key: "addr"] = TokenType.AddressLiteral,

            // Floating-point
            [key: "f16"] = TokenType.F16Literal,
            [key: "f32"] = TokenType.F32Literal,
            [key: "f64"] = TokenType.F64Literal,
            [key: "f128"] = TokenType.F128Literal,

            // Decimal floating-point
            [key: "d32"] = TokenType.D32Literal,
            [key: "d64"] = TokenType.D64Literal,
            [key: "d128"] = TokenType.D128Literal,

            // Imaginary (for complex numbers)
            // RF defaults "j" to J64Literal, SF defaults to JnLiteral
            [key: "j"] = _language == Language.RazorForge
                ? TokenType.J64Literal
                : TokenType.JnLiteral,
            [key: "j32"] = TokenType.J32Literal,
            [key: "j64"] = TokenType.J64Literal,
            [key: "j128"] = TokenType.J128Literal,
            [key: "jn"] = TokenType.JnLiteral
        };
    }

    /// <summary>
    /// Gets a value indicating whether the source code is in script mode (no definitions found).
    /// </summary>
    public bool IsScriptMode => !_hasDefinitions;

    #endregion

    #region Source Validation

    /// <summary>
    /// Normalizes source text before scanning and rejects invisible or ambiguous input.
    /// </summary>
    private static string NormalizeAndValidateSource(string source,
        string fileName,
        Language language)
    {
        if (source.Length > 0 && source[index: 0] == '\uFEFF')
        {
            source = source[1..];
        }

        source = source.Replace(oldValue: "\r\n", newValue: "\n")
                       .Replace(oldChar: '\r', newChar: '\n');

        for (int i = 0; i < source.Length; i += 1)
        {
            ValidateSourceCharacter(source: source,
                fileName: fileName,
                language: language,
                position: i);
        }

        return source;
    }

    /// <summary>
    /// Rejects a single invisible or ambiguous source character (null byte, tab, unsupported
    /// whitespace, or a Unicode format character) at the given position.
    /// </summary>
    private static void ValidateSourceCharacter(string source,
        string fileName,
        Language language,
        int position)
    {
        char c = source[index: position];
        if (c == '\0')
        {
            ThrowInvalidSourceCharacter(source: source,
                fileName: fileName,
                language: language,
                position: position,
                message: "Source contains a null byte");
        }

        if (c == '\t')
        {
            ThrowInvalidSourceCharacter(source: source,
                fileName: fileName,
                language: language,
                position: position,
                message: "Tabs are not allowed; indentation must use spaces");
        }

        if (char.IsWhiteSpace(c: c) && c is not ' ' and not '\n')
        {
            ThrowInvalidSourceCharacter(source: source,
                fileName: fileName,
                language: language,
                position: position,
                message: $"Unsupported whitespace character U+{(int)c:X4}");
        }

        if (CharUnicodeInfo.GetUnicodeCategory(ch: c) == UnicodeCategory.Format)
        {
            ThrowInvalidSourceCharacter(source: source,
                fileName: fileName,
                language: language,
                position: position,
                message: $"Unsupported format character U+{(int)c:X4}");
        }
    }

    private static void ThrowInvalidSourceCharacter(string source,
        string fileName,
        Language language,
        int position,
        string message)
    {
        int line = 1;
        int column = 1;
        for (int i = 0; i < position; i += 1)
        {
            if (source[index: i] == '\n')
            {
                line += 1;
                column = 1;
            }
            else
            {
                column += 1;
            }
        }

        throw new GrammarException(code: GrammarDiagnosticCode.InvalidCharacter,
            message: message,
            fileName: fileName,
            line: line,
            column: column,
            language: language);
    }

    #endregion

    #region Public memberRoutines

    /// <summary>
    /// Tokenizes the entire source code and returns a list of tokens.
    /// </summary>
    /// <returns>A list of tokens representing the tokenized source code, ending with EOF.</returns>
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
        while (_indentStack.Count > 1)
        {
            _indentStack.Pop();
            AddToken(type: TokenType.Dedent, text: "");
        }

        _tokens.Add(item: new Token(Type: TokenType.Eof,
            FileName: _fileName,
            Text: "",
            Line: _line,
            Column: _column,
            Position: _position));
        return _tokens;
    }

    #endregion
}
