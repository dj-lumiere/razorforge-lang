using Builder.Diagnostics;

namespace Builder.Tokenizer;

/// <summary>
/// Partial class containing numeric literal scanning memberRoutines for the unified tokenizer.
/// </summary>
/// <remarks>
/// Key language-conditional: unsuffixed defaults differ between RF and SF.
/// RF: integer -> S64Literal, float -> F64Literal
/// SF: integer -> Integer, float -> Decimal
/// </remarks>
public partial class Tokenizer
{
    #region Decimal Numbers

    /// <summary>
    /// Scans a decimal numeric literal, handling integers, floats, and suffixed numbers.
    /// </summary>
    private void ScanNumber()
    {
        // Consume digits and underscores
        while (char.IsDigit(c: Peek()) || Peek() == '_')
        {
            Advance();
        }

        bool isFloat = ScanDecimalFractionalPart();
        if (ScanScientificNotation())
        {
            isFloat = true;
        }

        // Skip underscore before suffix after scientific notation (e.g., 3.4e10_f64)
        if (Peek() == '_' && char.IsLetter(c: Peek(offset: 1)))
        {
            Advance();
        }

        // Check for type suffix
        if (char.IsLetter(c: Peek()))
        {
            EmitDecimalSuffixedToken(isFloat: isFloat);
        }
        else
        {
            AddToken(type: isFloat
                ? TokenType.UndecidedDecimal
                : TokenType.UndecidedInteger);
        }
    }

    /// <summary>
    /// Scans the optional fractional part of a decimal literal (e.g. the <c>.25</c> in <c>3.25</c>).
    /// </summary>
    /// <returns><c>true</c> if a fractional part was consumed.</returns>
    private bool ScanDecimalFractionalPart()
    {
        if (Peek() != '.' || !char.IsDigit(c: Peek(offset: 1)))
        {
            return false;
        }

        Advance(); // consume '.'
        while (char.IsDigit(c: Peek()) || Peek() == '_')
        {
            Advance();
        }

        return true;
    }

    /// <summary>
    /// Scans the optional scientific-notation exponent (<c>e</c>/<c>E</c> followed by an optional
    /// sign and exponent digits).
    /// </summary>
    /// <returns><c>true</c> if a scientific-notation suffix was consumed.</returns>
    private bool ScanScientificNotation()
    {
        if (Peek() != 'e' && Peek() != 'E')
        {
            return false;
        }

        Advance(); // consume 'e' or 'E'
        if (Peek() == '+' || Peek() == '-')
        {
            Advance();
        }

        while (char.IsDigit(c: Peek()))
        {
            Advance();
        }

        return true;
    }

    /// <summary>
    /// Consumes a decimal-number type suffix and emits the matching token, dispatching over
    /// arbitrary-precision, numeric, memory-size, and duration suffix tables. Throws on an
    /// unknown suffix. Assumes the caller has confirmed a letter begins the suffix.
    /// </summary>
    private void EmitDecimalSuffixedToken(bool isFloat)
    {
        int suffixStart = _position;
        while (char.IsLetterOrDigit(c: Peek()))
        {
            Advance();
        }

        string suffix =
            _source.Substring(startIndex: suffixStart, length: _position - suffixStart);

        // Arbitrary precision: `n` → Integer (integer syntax only — `1.5n`
        // names no integer), `dn` → Decimal (both syntaxes: `1dn` and
        // `0.5dn` are equally unambiguous, like every other typed suffix).
        if (!isFloat && suffix == ArbitraryIntegerSuffix)
        {
            AddToken(type: TokenType.IntegerLiteral);
        }
        else if (suffix == ArbitraryDecimalSuffix)
        {
            AddToken(type: TokenType.DecimalLiteral);
        }
        else if (_numericSuffixToTokenType.TryGetValue(key: suffix,
                     value: out TokenType numericType))
        {
            AddToken(type: numericType);
        }
        else if (_byteSizeSuffixToTokenType.TryGetValue(key: suffix,
                     value: out TokenType memoryType))
        {
            AddToken(type: memoryType);
        }
        else if (_durationSuffixToTokenType.TryGetValue(key: suffix,
                     value: out TokenType durationToken))
        {
            AddToken(type: durationToken);
        }
        else
        {
            throw new GrammarException(code: ClassifySuffixError(suffix: suffix, isFloat: isFloat),
                message: $"Unknown suffix '{suffix}'",
                fileName: _fileName,
                line: _line,
                column: _column,
                language: _language);
        }
    }

    #endregion

    #region Prefixed Numbers

    /// <summary>
    /// Scans a prefixed numeric literal (hexadecimal, binary, or hex float).
    /// Hex floats use C99 format: 0x1.ABCDp5 (hex mantissa + binary exponent).
    /// </summary>
    private void ScanPrefixedNumber(bool isHex)
    {
        bool isHexFloat = false;

        // Consume valid digits and underscores
        if (isHex)
        {
            isHexFloat = ScanHexDigitsAndFloatParts();
        }
        else
        {
            while (Peek() == '0' || Peek() == '1' || Peek() == '_')
            {
                Advance();
            }
        }

        // Skip underscore before suffix (e.g., 0x1.0p5_f64)
        if (Peek() == '_' && char.IsLetter(c: Peek(offset: 1)))
        {
            Advance();
        }

        // Check for type suffix
        if (char.IsLetter(c: Peek()))
        {
            EmitPrefixedSuffixedToken(isHex: isHex, isHexFloat: isHexFloat);
        }
        else
        {
            AddToken(type: isHexFloat
                ? TokenType.UndecidedDecimal
                : TokenType.UndecidedInteger);
        }
    }

    /// <summary>
    /// Consumes hex digits/underscores plus any hex-float fractional part (0x1.ABCD) and binary
    /// exponent (p5), treating an underscore that precedes a known type suffix as the suffix
    /// separator rather than a digit separator.
    /// </summary>
    /// <returns><c>true</c> if a hex-float fractional part or exponent was seen.</returns>
    private bool ScanHexDigitsAndFloatParts()
    {
        ScanHexIntegerDigits();
        bool isHexFloat = ScanHexFractionalPart();
        if (ScanHexBinaryExponent())
        {
            isHexFloat = true;
        }

        return isHexFloat;
    }

    /// <summary>
    /// Consumes hex digits and underscores, stopping early when an underscore introduces a type
    /// suffix (e.g. <c>_addr</c>) rather than a digit separator (e.g. <c>_ABCD</c>).
    /// </summary>
    private void ScanHexIntegerDigits()
    {
        while (IsHexDigit(c: Peek()) || Peek() == '_')
        {
            // When encountering underscore in hex mode, check if what follows
            // is a type suffix (e.g., _addr) rather than a digit separator (e.g., _ABCD)
            if (Peek() == '_' && UnderscoreIntroducesSuffix())
            {
                Advance(); // consume the underscore
                break; // suffix follows
            }

            Advance();
        }
    }

    /// <summary>
    /// Scans the optional hex-float fractional part (e.g. the <c>.ABCD</c> in <c>0x1.ABCDp5</c>).
    /// </summary>
    /// <returns><c>true</c> if a fractional part was consumed.</returns>
    private bool ScanHexFractionalPart()
    {
        if (Peek() != '.' || !IsHexDigit(c: Peek(offset: 1)))
        {
            return false;
        }

        Advance(); // consume '.'
        while (IsHexDigit(c: Peek()) || Peek() == '_')
        {
            Advance();
        }

        return true;
    }

    /// <summary>
    /// Scans the optional hex-float binary exponent (<c>p</c>/<c>P</c> with optional sign and
    /// decimal exponent digits, e.g. the <c>p5</c> in <c>0x1.0p5</c>).
    /// </summary>
    /// <returns><c>true</c> if a binary exponent was consumed.</returns>
    private bool ScanHexBinaryExponent()
    {
        if (Peek() != 'p' && Peek() != 'P')
        {
            return false;
        }

        Advance(); // consume 'p'/'P'
        if (Peek() == '+' || Peek() == '-')
        {
            Advance();
        }

        while (char.IsDigit(c: Peek()))
        {
            Advance();
        }

        return true;
    }

    /// <summary>
    /// Peeks whether the underscore at the current position introduces a known type suffix
    /// (e.g., <c>_addr</c>) rather than serving as a hex digit separator (e.g., <c>_ABCD</c>).
    /// </summary>
    private bool UnderscoreIntroducesSuffix()
    {
        int lookAhead = 1;
        while (char.IsLetterOrDigit(c: Peek(offset: lookAhead)))
        {
            lookAhead++;
        }

        if (lookAhead > 1)
        {
            string candidate = _source.Substring(startIndex: _position + 1, length: lookAhead - 1);
            if (_numericSuffixToTokenType.ContainsKey(key: candidate) ||
                candidate == ArbitraryIntegerSuffix || candidate == ArbitraryDecimalSuffix)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Consumes a prefixed-number (hex/binary) type suffix and emits the matching token. Throws on
    /// an unknown suffix. Assumes the caller has confirmed a letter begins the suffix.
    /// </summary>
    private void EmitPrefixedSuffixedToken(bool isHex, bool isHexFloat)
    {
        int suffixStart = _position;
        while (char.IsLetterOrDigit(c: Peek()))
        {
            Advance();
        }

        string suffix =
            _source.Substring(startIndex: suffixStart, length: _position - suffixStart);

        // Arbitrary precision: `n` for hex integers, `dn` for Decimal (the
        // integer form is only reachable via the explicit `_dn` spelling —
        // a bare `d` is a hex digit and gets consumed by the mantissa).
        if (!isHexFloat && suffix == ArbitraryIntegerSuffix)
        {
            AddToken(type: TokenType.IntegerLiteral);
        }
        else if (suffix == ArbitraryDecimalSuffix)
        {
            AddToken(type: TokenType.DecimalLiteral);
        }
        else if (_numericSuffixToTokenType.TryGetValue(key: suffix,
                     value: out TokenType tokenType))
        {
            AddToken(type: tokenType);
        }
        else
        {
            string baseType = isHex
                ? "hex"
                : "binary";
            throw new GrammarException(code: GrammarDiagnosticCode.InvalidNumericLiteral,
                message: $"Unknown {baseType} suffix '{suffix}'",
                fileName: _fileName,
                line: _line,
                column: _column,
                language: _language);
        }
    }

    /// <summary>
    /// Scans an octal numeric literal (0o prefix).
    /// </summary>
    private void ScanOctalNumber()
    {
        // Consume valid octal digits and underscores
        while (Peek() >= '0' && Peek() <= '7' || Peek() == '_')
        {
            Advance();
        }

        // Check for type suffix
        if (char.IsLetter(c: Peek()))
        {
            int suffixStart = _position;
            while (char.IsLetterOrDigit(c: Peek()))
            {
                Advance();
            }

            string suffix =
                _source.Substring(startIndex: suffixStart, length: _position - suffixStart);

            // Handle arbitrary precision suffix (n) - always Integer for octal
            if (suffix == ArbitraryIntegerSuffix)
            {
                AddToken(type: TokenType.IntegerLiteral);
            }
            else if (_numericSuffixToTokenType.TryGetValue(key: suffix,
                         value: out TokenType tokenType))
            {
                AddToken(type: tokenType);
            }
            else
            {
                throw new GrammarException(code: GrammarDiagnosticCode.InvalidNumericLiteral,
                    message: $"Unknown octal suffix '{suffix}'",
                    fileName: _fileName,
                    line: _line,
                    column: _column,
                    language: _language);
            }
        }
        else
        {
            AddToken(type: TokenType.UndecidedInteger);
        }
    }

    #endregion

    #region Suffix Error Classification

    /// <summary>
    /// Classifies an unknown numeric suffix into the most specific diagnostic code.
    /// </summary>
    private static GrammarDiagnosticCode ClassifySuffixError(string suffix, bool isFloat)
    {
        char first = char.ToLowerInvariant(c: suffix[index: 0]);

        // Memory-unit-like suffixes (b, k, m, g)
        if (first is 'b' or 'k' or 'm' or 'g')
        {
            return GrammarDiagnosticCode.InvalidMemoryLiteral;
        }

        // Duration-like suffixes (w, d, h, s, or contains ms/us/ns)
        if (first is 'w' or 'd' or 'h' or 's')
        {
            return GrammarDiagnosticCode.InvalidDurationLiteral;
        }

        string lower = suffix.ToLowerInvariant();
        if (lower.Contains(value: "ms") || lower.Contains(value: "us") ||
            lower.Contains(value: "ns"))
        {
            return GrammarDiagnosticCode.InvalidDurationLiteral;
        }

        // Float with decimal point
        if (isFloat)
        {
            return GrammarDiagnosticCode.InvalidFloatLiteral;
        }

        return GrammarDiagnosticCode.InvalidNumericLiteral;
    }

    #endregion
}
