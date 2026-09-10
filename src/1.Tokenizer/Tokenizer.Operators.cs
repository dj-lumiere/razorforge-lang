namespace Builder.Tokenizer;

/// <summary>
/// Partial class containing operator scanning memberRoutines for the unified tokenizer.
/// </summary>
/// <remarks>
/// <para>
/// Both languages support the same operator set, except RazorForge does not have checked operators.
/// </para>
/// <list type="bullet">
///   <item><description>Arithmetic: +, -, *, /, %, **</description></item>
///   <item><description>Overflow variants: +%, -%, *% (wrap), +^, -^, *^, /^ (clamping)</description></item>
///   <item><description>Comparison: ==, !=, &lt;, &gt;, &lt;=, &lt;=&gt;, &gt;=</description></item>
///   <item><description>Bitwise: &amp;, |, ^, ~, &lt;&lt;, &gt;&gt;, &lt;&lt;&lt;, &gt;&gt;&gt;</description></item>
///   <item><description>Special: -&gt;, =&gt;, @native</description></item>
/// </list>
/// </remarks>
public partial class Tokenizer
{
    #region Arithmetic Operators

    /// <summary>
    /// Scans a plus-based operator (+, +%, +^, +=, +%=, +^=).
    /// </summary>
    private void ScanPlusOperator()
    {
        switch (Peek())
        {
            case '%':
                Advance();
                AddToken(type: Match(expected: '=')
                    ? TokenType.PlusWrapAssign
                    : TokenType.PlusWrap);
                break;
            case '^':
                Advance();
                AddToken(type: Match(expected: '=')
                    ? TokenType.PlusClampAssign
                    : TokenType.PlusClamp);
                break;
            case '=':
                Advance();
                AddToken(type: TokenType.PlusAssign);
                break;
            case '!':
                Advance();
                AddToken(type: TokenType.PlusUnchecked);
                break;
            default:
                AddToken(type: TokenType.Plus);
                break;
        }
    }

    /// <summary>
    /// Scans a minus-based operator (-, -%, -^, -=, -%=, -^=).
    /// </summary>
    /// <remarks>
    /// Arrow (-&gt;) is handled separately in ScanToken.
    /// </remarks>
    private void ScanMinusOperator()
    {
        switch (Peek())
        {
            case '%':
                Advance();
                AddToken(type: Match(expected: '=')
                    ? TokenType.MinusWrapAssign
                    : TokenType.MinusWrap);
                break;
            case '^':
                Advance();
                AddToken(type: Match(expected: '=')
                    ? TokenType.MinusClampAssign
                    : TokenType.MinusClamp);
                break;
            case '=':
                Advance();
                AddToken(type: TokenType.MinusAssign);
                break;
            case '!':
                Advance();
                AddToken(type: TokenType.MinusUnchecked);
                break;
            default:
                AddToken(type: TokenType.Minus);
                break;
        }
    }

    /// <summary>
    /// Scans a star-based operator (*, **, *%, *^, **%, **^, *=, **=, *%=, **%=, *^=, **^=).
    /// </summary>
    private void ScanStarOperator()
    {
        bool isPow = Match(expected: '*'); // Check for **

        switch (Peek())
        {
            case '%':
                ScanStarWrapOperator(isPow: isPow);
                break;
            case '^':
                ScanStarClampOperator(isPow: isPow);
                break;
            case '=':
                Advance();
                // *= or **=
                AddToken(type: isPow
                    ? TokenType.PowerAssign
                    : TokenType.StarAssign);
                break;
            case '!':
                Advance();
                // *! or **!
                AddToken(type: isPow
                    ? TokenType.PowerUnchecked
                    : TokenType.MultiplyUnchecked);
                break;
            default:
                AddToken(type: isPow
                    ? TokenType.Power
                    : TokenType.Star);
                break;
        }
    }

    /// <summary>
    /// Scans the wrapping star/power variants (*%, **%, *%=, **%=) after the leading '%'.
    /// </summary>
    private void ScanStarWrapOperator(bool isPow)
    {
        Advance();
        // *%= or **%=
        if (Match(expected: '='))
        {
            AddToken(type: isPow
                ? TokenType.PowerWrapAssign
                : TokenType.MultiplyWrapAssign);
        }
        else
        {
            AddToken(type: isPow
                ? TokenType.PowerWrap
                : TokenType.MultiplyWrap);
        }
    }

    /// <summary>
    /// Scans the clamping star/power variants (*^, **^, *^=, **^=) after the leading '^'.
    /// </summary>
    private void ScanStarClampOperator(bool isPow)
    {
        Advance();
        // *^= or **^=
        if (Match(expected: '='))
        {
            AddToken(type: isPow
                ? TokenType.PowerClampAssign
                : TokenType.MultiplyClampAssign);
        }
        else
        {
            AddToken(type: isPow
                ? TokenType.PowerClamp
                : TokenType.MultiplyClamp);
        }
    }

    /// <summary>
    /// Scans a slash-based operator (/, /^, //, /=, /^=, //=).
    /// </summary>
    private void ScanSlashOperator()
    {
        if (!Match(expected: '/'))
        {
            // Single / - check for /^, /^=, /=, /!
            if (Match(expected: '^'))
            {
                AddToken(type: Match(expected: '=')
                    ? TokenType.SlashClampAssign
                    : TokenType.SlashClamp);
            }
            else if (Match(expected: '='))
            {
                AddToken(type: TokenType.SlashAssign);
            }
            else if (Match(expected: '!'))
            {
                AddToken(type: TokenType.SlashUnchecked); // /!
            }
            else
            {
                AddToken(type: TokenType.Slash);
            }
        }
        else
        {
            // Double // - check for //=, //!
            if (Match(expected: '='))
            {
                AddToken(type: TokenType.DivideAssign); // //=
            }
            else if (Match(expected: '!'))
            {
                AddToken(type: TokenType.DivideUnchecked); // //!
            }
            else
            {
                AddToken(type: TokenType.Divide); // //
            }
        }
    }

    /// <summary>
    /// Scans a percent-based operator (%, %=).
    /// </summary>
    private void ScanPercentOperator()
    {
        if (Peek() == '=')
        {
            Advance();
            AddToken(type: TokenType.PercentAssign);
        }
        else if (Peek() == '!')
        {
            Advance();
            AddToken(type: TokenType.PercentUnchecked);
        }
        else
        {
            AddToken(type: TokenType.Percent);
        }
    }

    #endregion

    #region Comparison and Shift Operators

    /// <summary>
    /// Scans operators starting with '&lt;'.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Recognizes:
    /// <list type="bullet">
    ///   <item><description>&lt; (Less) - less than comparison</description></item>
    ///   <item><description>&lt;= (LessEqual) - less than or equal</description></item>
    ///   <item><description>&lt;=&gt; (ThreeWayComparison) - spaceship operator</description></item>
    ///   <item><description>&lt;&lt; (LeftShift) - arithmetic left shift</description></item>
    ///   <item><description>&lt;&lt;&lt; (LogicalLeftShift) - logical left shift</description></item>
    /// </list>
    /// </para>
    /// </remarks>
    private void ScanLessThanOperator()
    {
        if (Match(expected: '='))
        {
            // <= or <=>
            if (Match(expected: '>'))
            {
                AddToken(type: TokenType.ThreeWayComparison); // <=>
            }
            else
            {
                AddToken(type: TokenType.LessEqual); // <=
            }
        }
        else if (Match(expected: '<'))
        {
            // << or <<< or <<= or <<<=
            if (Match(expected: '<'))
            {
                // <<< or <<<=
                AddToken(type: Match(expected: '=')
                    ? TokenType.LogicalLeftShiftAssign
                    : TokenType.LogicalLeftShift);
            }
            else if (Match(expected: '='))
            {
                AddToken(type: TokenType.LeftShiftAssign); // <<=
            }
            else
            {
                AddToken(type: TokenType.LeftShift); // <<
            }
        }
        else
        {
            AddToken(type: TokenType.Less);
        }
    }

    /// <summary>
    /// Scans operators starting with '&gt;' (&gt;, &gt;=, &gt;&gt;, &gt;&gt;&gt;, &gt;&gt;=, &gt;&gt;&gt;=).
    /// </summary>
    private void ScanGreaterThanOperator()
    {
        if (Match(expected: '='))
        {
            AddToken(type: TokenType.GreaterEqual);
        }
        else if (Match(expected: '>'))
        {
            // >> or >>> or >>= or >>>=
            if (Match(expected: '>'))
            {
                // >>> or >>>=
                AddToken(type: Match(expected: '=')
                    ? TokenType.LogicalRightShiftAssign
                    : TokenType.LogicalRightShift);
            }
            else if (Match(expected: '='))
            {
                AddToken(type: TokenType.RightShiftAssign); // >>=
            }
            else
            {
                AddToken(type: TokenType.RightShift); // >>
            }
        }
        else
        {
            AddToken(type: TokenType.Greater);
        }
    }

    #endregion

    #region Special Operators

    /// <summary>
    /// Scans tokens starting with '@' (annotation marker).
    /// </summary>
    private void ScanAtSign()
    {
        AddToken(type: TokenType.At);
    }

    #endregion
}
