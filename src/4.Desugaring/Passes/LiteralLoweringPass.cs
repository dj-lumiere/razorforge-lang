using Compiler.Tokenizer;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Compiler.Desugaring.Passes;

/// <summary>
/// Lowers domain-specific literal tokens to equivalent record constructor expressions
/// before codegen. After this pass, codegen never sees ByteSize, Duration, Character,
/// or ByteLetter literals -> only <see cref="CreatorExpression"/> nodes.
///
/// <para>Handled token types:</para>
/// <list type="bullet">
/// <item>ByteSize (<c>64kib</c>, <c>100mb</c>, ?? -> <c>ByteSize(value: bytes_u64)</c></item>
/// <item>Duration (<c>5s</c>, <c>100ms</c>, ?? -> <c>Duration(seconds: s64, nanoseconds: u32)</c></item>
/// <item>Character (<c>'a'</c>) -> <c>Character(from: codepoint_u32)</c></item>
/// <item>ByteLetter (<c>b'x'</c>) -> <c>Byte(from: byte_u8)</c></item>
/// </list>
///
/// <para><c>Bytes</c> literals (<c>b"..."</c>) are not lowered here -> they produce global-constant
/// entity allocations and remain in codegen.</para>
/// </summary>
internal sealed class LiteralLoweringPass : AstRewriter
{
    private readonly Dictionary<string, Statement>? _variantBodies;
    // Arbitrary-precision literal lowering: `123n`/`3.14dn` -> Integer/Decimal.from_literal(text:"...").
    private const string FromLiteralRoutine = "from_literal";
    private readonly TypeInfo? _integerType;
    private readonly TypeInfo? _textType;
    private readonly RoutineInfo? _integerFromLiteral;
    // Imaginary literal lowering: `4.0j64` -> C64(real: 0.0_f64, imag: 4.0_f64), etc.
    private readonly TypeInfo? _c32Type;
    private readonly TypeInfo? _c64Type;
    private readonly TypeInfo? _c128Type;
    private readonly TypeInfo? _complexType;
    private readonly TypeInfo? _f32Type;
    private readonly TypeInfo? _f64Type;
    private readonly TypeInfo? _f128Type;
    // `jn` imaginary literals build a Complex whose components are arbitrary-precision Real.
    private readonly TypeInfo? _realType;
    private readonly RoutineInfo? _realFromLiteral;
    // Domain-literal record types, stamped onto the lowered CreatorExpression's ResolvedType. Without this
    // the creator carries no type, and any pipeline copy that reaches OperatorLoweringPass WITHOUT first
    // running CallOverloadResolutionPass (which is what otherwise fills a creator's ResolvedType) lowers a
    // comparison like `'a' < 'b'` to an UNRESOLVED `.lt` call (LoweringKind=Unknown, no ResolvedRoutine) —
    // which then hard-errors at codegen. Stamping the type here makes the operand type flow deterministically.
    private readonly TypeInfo? _characterType;
    private readonly TypeInfo? _byteType;
    private readonly TypeInfo? _byteSizeType;
    private readonly TypeInfo? _durationType;

    /// <summary>
    /// Initializes a new instance with the dependencies required for its compiler phase.
    /// </summary>
    internal LiteralLoweringPass(PostprocessingContext ctx)
    {
        _variantBodies = ctx.VariantBodies;
        // `n`/`dn` arbitrary-precision literals are emitted as malformed scalar IR by codegen
        // (e.g. `store %Record.Integer 42n`); lower them to an infallible constructor call instead.
        // Integer/Complex/Real live in `module Numerics` — qualify (a bare lookup depended on the
        // cross-module short-name scan; scan-off it missed, the `42n`/`jn` literal lowering was skipped,
        // and the raw arbitrary-precision literal reached codegen as malformed IR — `store %Record 42n`).
        _integerType = ctx.Registry.LookupType(name: "Numerics.Integer")
                       ?? ctx.Registry.LookupType(name: "Integer");
        _textType = ctx.Registry.LookupType(name: "Text");
        _integerFromLiteral = _integerType != null
            ? ctx.Registry.LookupMemberRoutine(type: _integerType, memberRoutineName: FromLiteralRoutine)
            : null;

        // Imaginary `j*` literals (J32/J64/J128/Jn) are emitted as Text by codegen (no scalar form
        // for the complex record types); lower them to pure-imaginary complex constructors.
        _c32Type = ctx.Registry.LookupType(name: "C32");
        _c64Type = ctx.Registry.LookupType(name: "C64");
        _c128Type = ctx.Registry.LookupType(name: "C128");
        _complexType = ctx.Registry.LookupType(name: "Numerics.Complex")
                       ?? ctx.Registry.LookupType(name: "Complex");
        _f32Type = ctx.Registry.LookupType(name: "F32");
        _f64Type = ctx.Registry.LookupType(name: "F64");
        _f128Type = ctx.Registry.LookupType(name: "F128");
        _realType = ctx.Registry.LookupType(name: "Numerics.Real")
                    ?? ctx.Registry.LookupType(name: "Real");
        _realFromLiteral = _realType != null
            ? ctx.Registry.LookupMemberRoutine(type: _realType, memberRoutineName: FromLiteralRoutine)
            : null;

        _characterType = ctx.Registry.LookupType(name: "Character");
        _byteType = ctx.Registry.LookupType(name: "Byte");
        _byteSizeType = ctx.Registry.LookupType(name: "ByteSize");
        _durationType = ctx.Registry.LookupType(name: "Duration");
    }

    // -----------------------------------------------------------------------------

    /// <summary>
    /// Runs this compiler phase over its configured input.
    /// </summary>
    public void Run(Program program)
        => BodyDispatch.RunOnProgram(program, lower: r => VisitStatement(r.Body));

    /// <summary>
    /// Runs this compiler phase over its configured input.
    /// </summary>
    public void RunOnVariantBodies()
    {
        if (_variantBodies == null) return;
        BodyDispatch.RunOnVariantBodies(_variantBodies, lower: (_, body) => VisitStatement(body));
    }

    // -----------------------------------------------------------------------------

    /// <summary>
    /// The nodes this pass actually rewrites. A <see cref="LiteralExpression"/> is a leaf to the base
    /// rewriter, but it is the primary transform here (domain literals -> constructor calls). A
    /// <see cref="CarrierPayloadExpression"/> is not a node the base rewriter recurses into, so it is
    /// handled here to keep its <c>Carrier</c> child lowered. All other structural recursion is supplied
    /// by <see cref="AstRewriter"/>.
    /// </summary>
    public override Expression VisitExpression(Expression expr)
    {
        switch (expr)
        {
            case LiteralExpression literal:
            {
                // Arbitrary-precision `n`/`dn` literals -> infallible from_literal constructor call
                // (instance lowering: needs the cached Integer/Decimal types + routines).
                Expression? fromLit = TryLowerArbitraryPrecisionLiteral(literal);
                if (fromLit != null) return fromLit;

                // Imaginary `j*` literals -> pure-imaginary complex constructor.
                CreatorExpression? imag = TryLowerImaginaryLiteral(literal);
                if (imag != null) return imag;

                Expression? lowered = TryLowerLiteral(literal);
                if (lowered != null) return lowered;
                return expr;
            }
            case CarrierPayloadExpression cpe:
            {
                Expression c = VisitExpression(cpe.Carrier);
                return ReferenceEquals(c, cpe.Carrier) ? expr : cpe with { Carrier = c };
            }
            default:
                return base.VisitExpression(expr);
        }
    }

    protected override Expression VisitBackIndex(BackIndexExpression e)
    {
        // `^n` is NOT materialized into a value here — it stays a `BackIndexExpression` marker.
        // OperatorLoweringPass (runs after this pass) rewrites the enclosing subscript/slice to
        // `back_resolve(count: coll.count(), offset: n)`. Retag an untyped/signed integer-literal
        // offset to U64 (the `^n` position is U64) BEFORE lowering, so it stays a scalar i64 and
        // is not lowered to an arbitrary-precision Integer (which is heap/Text-backed).
        Expression operand = e.Operand is LiteralExpression
            {
                LiteralType: TokenType.UndecidedInteger or TokenType.IntegerLiteral
                    or TokenType.S64Literal
            } lit
            ? lit with { LiteralType = TokenType.U64Literal }
            : e.Operand;
        Expression o = VisitExpression(operand);
        return e with { Operand = o };
    }

    // -----------------------------------------------------------------------------

    /// <summary>
    /// Attempts to lower literal and reports whether it succeeded.
    /// </summary>
    private CreatorExpression? TryLowerLiteral(LiteralExpression literal)
    {
        SourceLocation loc = literal.Location;

        switch (literal.Value)
        {
            case char ch:
                return MakeCharacterCreator(char.ConvertToUtf32(ch.ToString(), 0), loc);

            case string s when IsByteSizeLiteralType(literal.LiteralType):
                return MakeByteSizeCreator(s, loc);

            case string s when IsDurationLiteralType(literal.LiteralType):
                return MakeDurationCreator(s, literal.LiteralType, loc);

            case string s when literal.LiteralType == TokenType.CharacterLiteral:
                return MakeCharacterCreator(s.Length > 0 ? char.ConvertToUtf32(s, 0) : 0, loc);

            case string s when literal.LiteralType == TokenType.ByteLetterLiteral:
                return MakeByteCreator(s.Length > 0 ? s[0] & 0xFF : 0, loc);
        }

        return null;
    }

    /// <summary>
    /// Lowers an arbitrary-precision <c>n</c>/<c>dn</c> literal to a <c>from_literal</c> constructor
    /// call, or returns null if <paramref name="literal"/> is not such a literal (or the
    /// Integer/Decimal type/routine is unavailable). Codegen has no scalar form for these record
    /// types, so they must become a call before codegen.
    /// </summary>
    private CallExpression? TryLowerArbitraryPrecisionLiteral(LiteralExpression literal)
    {
        if (literal.Value is not string s) return null;
        SourceLocation loc = literal.Location;
        return literal.LiteralType switch
        {
            TokenType.IntegerLiteral when _integerType != null && _integerFromLiteral != null =>
                MakeFromLiteralCall(s, "n", _integerType, _integerFromLiteral, loc),
            // Suflae: an UNSUFFIXED integer literal that SA resolved to `Integer` (the SF default; RF
            // defaults to S64) is still `UndecidedInteger` at this pass — ExpressionLoweringPass only
            // rewrites the token to IntegerLiteral LATER, after this construction pass has already run.
            // So match the RESOLVED TYPE here and construct it too; otherwise codegen emits an invalid
            // raw `store <int> %Record.Integer` (Integer is a LibTomMath-handle record, not a scalar).
            TokenType.UndecidedInteger
                when literal.ResolvedType?.Name == "Integer"
                     && _integerType != null && _integerFromLiteral != null =>
                MakeFromLiteralCall(s, "n", _integerType, _integerFromLiteral, loc),
            // Decimal is now @llvm("i256") BID — its literals bake to a compile-time i256 constant
            // (NumericLiteralParser.EncodeDecimal) like D128, not a runtime from-string call.
            _ => null
        };
    }

    /// <summary>
    /// Lowers an imaginary <c>j*</c> literal (<c>4.0j32</c>/<c>4.0j64</c>/<c>4.0j</c>/<c>4.0j128</c>/
    /// <c>4.0jn</c>) to a pure-imaginary complex constructor <c>&lt;CType&gt;(real: 0, imag: value)</c>
    /// (C32/C64/C128 memberwise, or Complex for <c>jn</c>). Returns null if not such a literal or the
    /// types are unavailable. Codegen has no scalar form for the complex record types.
    /// </summary>
    private CreatorExpression? TryLowerImaginaryLiteral(LiteralExpression literal)
    {
        if (literal.Value is not string raw) return null;
        int j = raw.IndexOfAny(['j', 'J']);
        if (j < 0) return null;
        SourceLocation loc = literal.Location;
        // Magnitude = everything before the `j` suffix, underscores stripped.
        string mag = raw[..j].Replace(oldValue: "_", newValue: "");

        switch (literal.LiteralType)
        {
            case TokenType.J32Literal when _c32Type != null:
                return MakeComplexCreator("C32", _c32Type, mag, TokenType.F32Literal, _f32Type, loc);
            case TokenType.J64Literal when _c64Type != null:
                return MakeComplexCreator("C64", _c64Type, mag, TokenType.F64Literal, _f64Type, loc);
            case TokenType.J128Literal when _c128Type != null:
                return MakeComplexCreator("C128", _c128Type, mag, TokenType.F128Literal, _f128Type, loc);
            case TokenType.JnLiteral when _complexType != null && _realType != null
                                          && _realFromLiteral != null:
                // Complex components are arbitrary-precision Real -> from_literal calls
                // (the creator's args are not re-lowered, so build them already-lowered here).
                return new CreatorExpression("Complex", null,
                    [("real", MakeFromLiteralCall("0", "", _realType, _realFromLiteral, loc)),
                     ("imag", MakeFromLiteralCall(mag, "", _realType, _realFromLiteral, loc))],
                    loc) { ResolvedType = _complexType };
            default:
                return null;
        }
    }

    /// <summary>Builds <c>&lt;CType&gt;(real: 0&lt;suffix&gt;, imag: &lt;mag&gt;&lt;suffix&gt;)</c> for a
    /// fixed-width imaginary literal, where the components are float literals of <paramref name="compLit"/>.</summary>
    private static CreatorExpression MakeComplexCreator(string typeName, TypeInfo type, string mag,
        TokenType compLit, TypeInfo? compType, SourceLocation loc)
    {
        var real = new LiteralExpression(Value: "0.0", LiteralType: compLit, Location: loc) { ResolvedType = compType };
        var imag = new LiteralExpression(Value: mag, LiteralType: compLit, Location: loc) { ResolvedType = compType };
        return new CreatorExpression(typeName, null, [("real", real), ("imag", imag)], loc) { ResolvedType = type };
    }

    /// <summary>
    /// Builds <c>&lt;Type&gt;.from_literal(text: "&lt;digits&gt;")</c> for an arbitrary-precision
    /// (<c>n</c>/<c>dn</c>) literal. The suffix and digit-group underscores are stripped; the bare
    /// digit string is materialized at runtime by the infallible <c>from_literal</c> constructor.
    /// </summary>
    private CallExpression MakeFromLiteralCall(string raw, string suffix, TypeInfo type,
        RoutineInfo fromLiteral, SourceLocation loc)
    {
        string digits = (raw.EndsWith(value: suffix, comparisonType: StringComparison.OrdinalIgnoreCase)
            ? raw[..^suffix.Length]
            : raw).Replace(oldValue: "_", newValue: "");

        var textLit = new LiteralExpression(Value: digits, LiteralType: TokenType.TextLiteral, Location: loc)
        {
            ResolvedType = _textType
        };
        var arg = new NamedArgumentExpression(Name: "text", Value: textLit, Location: loc);
        var callee = new MemberExpression(
            Object: new IdentifierExpression(Name: type.Name, Location: loc) { ResolvedType = type },
            MemberName: FromLiteralRoutine, Location: loc);
        return new CallExpression(Callee: callee, Arguments: [arg], Location: loc)
        {
            ResolvedRoutine = fromLiteral,
            ResolvedType = fromLiteral.ReturnType
        };
    }

    /// <summary>
    /// Builds the make byte size creator used by later compiler work.
    /// </summary>
    private CreatorExpression MakeByteSizeCreator(string text, SourceLocation loc)
    {
        ulong bytes = ComputeByteSizeValue(text);
        var valueLit = new LiteralExpression(Value: bytes.ToString(), LiteralType: TokenType.U64Literal, Location: loc);
        return new CreatorExpression("ByteSize", null, [("value", valueLit)], loc) { ResolvedType = _byteSizeType };
    }

    /// <summary>
    /// Builds the make duration creator used by later compiler work.
    /// </summary>
    private CreatorExpression MakeDurationCreator(string text, TokenType literalType, SourceLocation loc)
    {
        (long seconds, long nanoseconds) = ComputeDurationValues(text, literalType);
        var secsLit = new LiteralExpression(Value: seconds.ToString(), LiteralType: TokenType.S64Literal, Location: loc);
        var nsLit = new LiteralExpression(Value: nanoseconds.ToString(), LiteralType: TokenType.U32Literal, Location: loc);
        return new CreatorExpression("Duration", null, [("seconds", secsLit), ("nanoseconds", nsLit)], loc) { ResolvedType = _durationType };
    }

    /// <summary>
    /// Builds the make character creator used by later compiler work.
    /// </summary>
    private CreatorExpression MakeCharacterCreator(int codepoint, SourceLocation loc)
    {
        var cpLit = new LiteralExpression(Value: codepoint.ToString(), LiteralType: TokenType.U32Literal, Location: loc);
        return new CreatorExpression("Character", null, [("from", cpLit)], loc) { ResolvedType = _characterType };
    }

    /// <summary>
    /// Builds the make byte creator used by later compiler work.
    /// </summary>
    private CreatorExpression MakeByteCreator(int byteValue, SourceLocation loc)
    {
        var byteLit = new LiteralExpression(Value: byteValue.ToString(), LiteralType: TokenType.U8Literal, Location: loc);
        return new CreatorExpression("Byte", null, [("from", byteLit)], loc) { ResolvedType = _byteType };
    }

    // -----------------------------------------------------------------------------

    /// <summary>
    /// Initializes a new instance with the dependencies required for its compiler phase.
    /// </summary>
    private static readonly (string Suffix, ulong Multiplier)[] ByteSizeSuffixes =
    [
        ("gib", 1_073_741_824UL),
        ("mib", 1_048_576UL),
        ("kib", 1_024UL),
        ("gb", 1_000_000_000UL),
        ("mb", 1_000_000UL),
        ("kb", 1_000UL),
        ("b", 1UL)
    ];

    /// <summary>
    /// Performs the compute byte size value step for this compiler phase.
    /// </summary>
    private static ulong ComputeByteSizeValue(string text)
    {
        string lower = text.ToLowerInvariant();
        foreach ((string suffix, ulong multiplier) in ByteSizeSuffixes)
        {
            if (!lower.EndsWith(suffix)) continue;
            string numPart = text[..^suffix.Length].TrimEnd('_').Replace("_", "");
            if (ulong.TryParse(numPart, out ulong value))
                return value * multiplier;
            break;
        }
        return 0;
    }

    /// <summary>
    /// Initializes a new instance with the dependencies required for its compiler phase.
    /// </summary>
    private static (long Seconds, long Nanoseconds) ComputeDurationValues(string text, TokenType literalType)
    {
        const long nsPerMicrosecond = 1_000L;
        const long nsPerMillisecond = 1_000_000L;
        const long nsPerSecond = 1_000_000_000L;
        const long secondsPerMinute = 60L;
        const long secondsPerHour = 3_600L;
        const long secondsPerDay = 86_400L;
        const long secondsPerWeek = 604_800L;

        string numericPart = literalType switch
        {
            TokenType.MillisecondLiteral => text[..^2],
            TokenType.MicrosecondLiteral => text[..^2],
            TokenType.NanosecondLiteral => text[..^2],
            _ => text[..^1]
        };
        numericPart = numericPart.Replace("_", "");
        if (!long.TryParse(numericPart, out long value)) value = 0;

        long seconds = 0, nanoseconds = 0;
        switch (literalType)
        {
            case TokenType.WeekLiteral:       seconds = value * secondsPerWeek; break;
            case TokenType.DayLiteral:        seconds = value * secondsPerDay; break;
            case TokenType.HourLiteral:       seconds = value * secondsPerHour; break;
            case TokenType.MinuteLiteral:     seconds = value * secondsPerMinute; break;
            case TokenType.SecondLiteral:     seconds = value; break;
            case TokenType.MillisecondLiteral:
                seconds = value / 1_000L;
                nanoseconds = (value % 1_000L) * nsPerMillisecond;
                break;
            case TokenType.MicrosecondLiteral:
                seconds = value / 1_000_000L;
                nanoseconds = (value % 1_000_000L) * nsPerMicrosecond;
                break;
            case TokenType.NanosecondLiteral:
                seconds = value / nsPerSecond;
                nanoseconds = value % nsPerSecond;
                break;
        }
        return (seconds, nanoseconds);
    }

    /// <summary>
    /// Returns whether is byte size literal type applies in the current compiler context.
    /// </summary>
    private static bool IsByteSizeLiteralType(TokenType type) =>
        type is TokenType.ByteLiteral or TokenType.KilobyteLiteral
            or TokenType.KibibyteLiteral or TokenType.MegabyteLiteral
            or TokenType.MebibyteLiteral or TokenType.GigabyteLiteral
            or TokenType.GibibyteLiteral;

    /// <summary>
    /// Returns whether is duration literal type applies in the current compiler context.
    /// </summary>
    private static bool IsDurationLiteralType(TokenType type) =>
        type is TokenType.WeekLiteral or TokenType.DayLiteral
            or TokenType.HourLiteral or TokenType.MinuteLiteral
            or TokenType.SecondLiteral or TokenType.MillisecondLiteral
            or TokenType.MicrosecondLiteral or TokenType.NanosecondLiteral;
}
