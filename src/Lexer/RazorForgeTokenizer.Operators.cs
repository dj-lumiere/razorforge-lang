namespace Compilers.RazorForge.Lexer;

using Compilers.Shared.Lexer;

/// <summary>
/// Partial class containing operator scanning methods for the RazorForge tokenizer.
/// </summary>
/// <remarks>
/// <para>
/// This file handles all operator recognition including:
/// <list type="bullet">
///   <item><description>Arithmetic operators: +, -, *, /, %, **</description></item>
///   <item><description>Overflow-checked variants: +?, -?, *?, //?, %?</description></item>
///   <item><description>Wrapping variants: +%, -%, *%</description></item>
///   <item><description>Saturating variants: +^, -^, *^</description></item>
///   <item><description>Comparison operators: ==, !=, &lt;, &gt;, &lt;=, &gt;=, &lt;=&gt;, ===, !==</description></item>
///   <item><description>Bitwise operators: &amp;, |, ^, ~, &lt;&lt;, &gt;&gt;, &lt;&lt;&lt;, &gt;&gt;&gt;</description></item>
///   <item><description>Special operators: -&gt;, =&gt;, @intrinsic, @native</description></item>
/// </list>
/// </para>
/// <para>
/// RazorForge provides explicit overflow handling through operator variants:
/// <list type="bullet">
///   <item><description>Default (no suffix): Implementation-defined behavior</description></item>
///   <item><description>? suffix: Checked - throws on overflow</description></item>
///   <item><description>% suffix: Wrapping - wraps around on overflow</description></item>
///   <item><description>^ suffix: Saturating - clamps to min/max on overflow</description></item>
/// </list>
/// </para>
/// </remarks>
public partial class RazorForgeTokenizer
{
    #region Arithmetic Operators

    /// <summary>
    /// Scans a plus-based operator, including overflow variants.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Recognizes:
    /// <list type="bullet">
    ///   <item><description>+ (Plus) - basic addition</description></item>
    ///   <item><description>+% (PlusWrap) - wrapping addition</description></item>
    ///   <item><description>+^ (PlusSaturate) - saturating addition</description></item>
    ///   <item><description>+? (PlusChecked) - checked addition</description></item>
    /// </list>
    /// </para>
    /// <para>The + character has already been consumed when this method is called.</para>
    /// </remarks>
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
    /// Scans a minus-based operator, including overflow variants.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Recognizes:
    /// <list type="bullet">
    ///   <item><description>- (Minus) - basic subtraction</description></item>
    ///   <item><description>-% (MinusWrap) - wrapping subtraction</description></item>
    ///   <item><description>-^ (MinusSaturate) - saturating subtraction</description></item>
    ///   <item><description>-? (MinusChecked) - checked subtraction</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// Note: The arrow operator (-&gt;) is handled separately in <see cref="ScanToken"/>
    /// before this method is called.
    /// </para>
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
    /// Scans a star-based operator, including power and overflow variants.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Recognizes:
    /// <list type="bullet">
    ///   <item><description>* (Star) - basic multiplication</description></item>
    ///   <item><description>** (Power) - exponentiation</description></item>
    ///   <item><description>*% (MultiplyWrap) - wrapping multiplication</description></item>
    ///   <item><description>*^ (MultiplySaturate) - saturating multiplication</description></item>
    ///   <item><description>*? (MultiplyChecked) - checked multiplication</description></item>
    ///   <item><description>**% (PowerWrap) - wrapping power</description></item>
    ///   <item><description>**^ (PowerSaturate) - saturating power</description></item>
    ///   <item><description>**? (PowerChecked) - checked power</description></item>
    /// </list>
    /// </para>
    /// </remarks>
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
    /// Scans a slash-based operator, distinguishing division from integer division.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Recognizes:
    /// <list type="bullet">
    ///   <item><description>/ (Slash) - floating-point division</description></item>
    ///   <item><description>// (Divide) - integer division</description></item>
    ///   <item><description>//? (DivideChecked) - checked integer division</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// Note: Unlike some languages, / and // have different semantics in RazorForge.
    /// Single slash is floating-point division, double slash is integer division.
    /// </para>
    /// </remarks>
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
    /// Scans a percent-based operator (modulo), including checked variant.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Recognizes:
    /// <list type="bullet">
    ///   <item><description>% (Percent) - modulo operation</description></item>
    ///   <item><description>%? (ModuloChecked) - checked modulo</description></item>
    /// </list>
    /// </para>
    /// </remarks>
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
            // << or <<? or <<<
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
    /// Scans operators starting with '&gt;'.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Recognizes:
    /// <list type="bullet">
    ///   <item><description>&gt; (Greater) - greater than comparison</description></item>
    ///   <item><description>&gt;= (GreaterEqual) - greater than or equal</description></item>
    ///   <item><description>&gt;&gt; (RightShift) - arithmetic right shift</description></item>
    ///   <item><description>&gt;&gt;&gt; (LogicalRightShift) - logical right shift</description></item>
    /// </list>
    /// </para>
    /// </remarks>
    private void ScanGreaterThanOperator()
    {
        if (Match('='))
        {
            AddToken(TokenType.GreaterEqual);
        }
        else if (Match('>'))
        {
            // >> or >>>
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
    /// Scans tokens starting with '@'.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Recognizes:
    /// <list type="bullet">
    ///   <item><description>@intrinsic - LLVM intrinsic operations</description></item>
    ///   <item><description>@native - C runtime library calls</description></item>
    ///   <item><description>@ (At) - standalone @ symbol</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// @intrinsic and @native are used in danger! blocks to access low-level
    /// operations and FFI calls respectively.
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
