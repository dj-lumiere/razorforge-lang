namespace Compilers.Suflae.Lexer;

using Compilers.Shared.Lexer;

/// <summary>
/// Partial class containing operator scanning methods for the Suflae tokenizer.
/// </summary>
/// <remarks>
/// <para>
/// Suflae supports the same operators as RazorForge, including overflow variants:
/// </para>
/// <list type="bullet">
///   <item><description>Arithmetic: +, -, *, /, %, **</description></item>
///   <item><description>Overflow variants: +%, -%, *% (wrap), +^, -^, *^ (saturate), +?, -?, *?, //?, %? (checked)</description></item>
///   <item><description>Comparison: ==, !=, &lt;, &gt;, &lt;=, &lt;=&gt;, &gt;=, ===, !==</description></item>
///   <item><description>Bitwise: &amp;, |, ^, ~, &lt;&lt;, &gt;&gt;, &lt;&lt;&lt;, &gt;&gt;&gt;</description></item>
///   <item><description>Special: -&gt;, =&gt;, @intrinsic, @native</description></item>
/// </list>
/// </remarks>
public partial class SuflaeTokenizer
{
    #region Arithmetic Operators

    /// <summary>
    /// Scans a plus-based operator (+, +%, +^, +?).
    /// </summary>
    private void ScanPlusOperator()
    {
        switch (Peek())
        {
            case '%':
                Advance();
                AddToken(TokenType.PlusWrap);
                break;
            case '^':
                Advance();
                AddToken(TokenType.PlusSaturate);
                break;
            case '?':
                Advance();
                AddToken(TokenType.PlusChecked);
                break;
            default:
                AddToken(TokenType.Plus);
                break;
        }
    }

    /// <summary>
    /// Scans a minus-based operator (-, -%, -^, -?).
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
                AddToken(TokenType.MinusWrap);
                break;
            case '^':
                Advance();
                AddToken(TokenType.MinusSaturate);
                break;
            case '?':
                Advance();
                AddToken(TokenType.MinusChecked);
                break;
            default:
                AddToken(TokenType.Minus);
                break;
        }
    }

    /// <summary>
    /// Scans a star-based operator (*, **, *%, *^, *?, **%, **^, **?).
    /// </summary>
    private void ScanStarOperator()
    {
        bool isPow = Match('*'); // Check for **

        switch (Peek())
        {
            case '%':
                Advance();
                AddToken(isPow ? TokenType.PowerWrap : TokenType.MultiplyWrap);
                break;
            case '^':
                Advance();
                AddToken(isPow ? TokenType.PowerSaturate : TokenType.MultiplySaturate);
                break;
            case '?':
                Advance();
                AddToken(isPow ? TokenType.PowerChecked : TokenType.MultiplyChecked);
                break;
            default:
                AddToken(isPow ? TokenType.Power : TokenType.Star);
                break;
        }
    }

    /// <summary>
    /// Scans a slash-based operator (/, //, //?).
    /// </summary>
    private void ScanSlashOperator()
    {
        if (!Match('/'))
        {
            AddToken(TokenType.Slash); // Single /
        }
        else
        {
            // Double //
            if (Peek() == '?')
            {
                Advance();
                AddToken(TokenType.DivideChecked); // //?
            }
            else
            {
                AddToken(TokenType.Divide); // //
            }
        }
    }

    /// <summary>
    /// Scans a percent-based operator (%, %?).
    /// </summary>
    private void ScanPercentOperator()
    {
        if (Peek() == '?')
        {
            Advance();
            AddToken(TokenType.ModuloChecked);
        }
        else
        {
            AddToken(TokenType.Percent);
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
    ///   <item><description>&lt;&lt;? (LeftShiftChecked) - checked left shift</description></item>
    ///   <item><description>&lt;&lt;&lt; (LogicalLeftShift) - logical left shift</description></item>
    /// </list>
    /// </para>
    /// </remarks>
    private void ScanLessThanOperator()
    {
        if (Match('='))
        {
            // <= or <=>
            if (Match('>'))
                AddToken(TokenType.ThreeWayComparison); // <=>
            else
                AddToken(TokenType.LessEqual); // <=
        }
        else if (Match('<'))
        {
            if (Match('<'))
                AddToken(TokenType.LogicalLeftShift); // <<<
            else if (Match('?'))
                AddToken(TokenType.LeftShiftChecked); // <<?
            else
                AddToken(TokenType.LeftShift); // <<
        }
        else
        {
            AddToken(TokenType.Less);
        }
    }

    /// <summary>
    /// Scans operators starting with '&gt;' (&gt;, &gt;=, &gt;&gt;, &gt;&gt;&gt;).
    /// </summary>
    private void ScanGreaterThanOperator()
    {
        if (Match('='))
        {
            AddToken(TokenType.GreaterEqual);
        }
        else if (Match('>'))
        {
            if (Match('>'))
                AddToken(TokenType.LogicalRightShift); // >>>
            else
                AddToken(TokenType.RightShift); // >>
        }
        else
        {
            AddToken(TokenType.Greater);
        }
    }

    #endregion

    #region Special Operators

    /// <summary>
    /// Scans tokens starting with '@' (@intrinsic, @native, or standalone @).
    /// </summary>
    /// <remarks>
    /// <para>
    /// While Suflae doesn't support danger! blocks, @intrinsic and @native
    /// are still recognized for potential future use or error reporting.
    /// </para>
    /// </remarks>
    private void ScanAtSign()
    {
        if (Peek() == 'i' && PeekWord() == "intrinsic")
        {
            // Consume "intrinsic" (9 characters)
            for (int i = 0; i < 9; i += 1)
                Advance();
            AddToken(TokenType.Intrinsic);
        }
        else if (Peek() == 'n' && PeekWord() == "native")
        {
            // Consume "native" (6 characters)
            for (int i = 0; i < 6; i += 1)
                Advance();
            AddToken(TokenType.Native);
        }
        else
        {
            AddToken(TokenType.At);
        }
    }

    #endregion
}
