namespace Compilers.Shared.Lexer;

/// <summary>
/// Defines all possible token types that can be produced by the lexical analyzer.
/// Each token represents a meaningful unit of source code that the parser can work with.
/// </summary>
public enum TokenType
{
    // ===== BASIC LITERALS =====

    // Cake default number types (arbitrary precision)
    /// <summary>Arbitrary precision integer in Cake - unsuffixed integers (42, 0xFF, 0b1010)</summary>
    Integer,

    /// <summary>Arbitrary precision decimal in Cake - unsuffixed decimals (3.14, 2.718)</summary>
    Decimal,

    /// <summary>Represents the semicolon (;) token in the syntax
    /// This token is present for unnecessary semicolon detection</summary>
    Semicolon,

    /// <summary>Variant declaration keyword (alias for Chimera)</summary>
    Variant,

    /// <summary>Mutant declaration keyword (alias for Chimera)</summary>
    Mutant,

    /// <summary>Single 8-bit character literal with explicit prefix (l8'a')</summary>
    Letter8Literal,

    /// <summary>Single 16-bit character literal with explicit prefix (l16'a')</summary>
    Letter16Literal,

    /// <summary>Single character literal - default for plain quotes ('a', '\n')</summary>
    LetterLiteral,

    // ===== STRING LITERALS =====
    // Basic 32-bit texts (default)
    /// <summary>Text literal - default for plain quotes ("hello world")</summary>
    TextLiteral,

    /// <summary>Formatted text with interpolation expressions (f"hello {name}")</summary>
    FormattedText,

    /// <summary>Raw text that doesn't process escape sequences (r"C:\path\file")</summary>
    RawText,

    /// <summary>Raw formatted text combining raw and interpolation (rf"path: {dir}\file")</summary>
    RawFormattedText,

    // Explicit width text literals
    /// <summary>8-bit text literal with explicit prefix (t8"hello")</summary>
    Text8Literal,

    /// <summary>8-bit raw text literal with explicit prefix (t8r"C:\path")</summary>
    Text8RawText,

    /// <summary>8-bit formatted text literal with explicit prefix (t8f"value: {x}")</summary>
    Text8FormattedText,

    /// <summary>8-bit raw formatted text literal with explicit prefix (t8rf"dir: {path}\file")</summary>
    Text8RawFormattedText,

    /// <summary>16-bit text literal with explicit prefix (t16"hello")</summary>
    Text16Literal,

    /// <summary>16-bit raw text literal with explicit prefix (t16r"C:\path")</summary>
    Text16RawText,

    /// <summary>16-bit formatted text literal with explicit prefix (t16f"value: {x}")</summary>
    Text16FormattedText,

    /// <summary>16-bit raw formatted text literal with explicit prefix (t16rf"dir: {path}\file")</summary>
    Text16RawFormattedText,

    // ===== TYPED NUMERIC LITERALS =====
    // Signed integers
    /// <summary>8-bit signed integer literal (42i8)</summary>
    S8Literal,

    /// <summary>16-bit signed integer literal (1000i16)</summary>
    S16Literal,

    /// <summary>32-bit signed integer literal (42i32)</summary>
    S32Literal,

    /// <summary>64-bit signed integer literal (1000i64)</summary>
    S64Literal,

    /// <summary>128-bit signed integer literal (42i128)</summary>
    S128Literal,

    /// <summary>System pointer-sized signed integer literal (42syssint)</summary>
    SyssintLiteral,

    // Unsigned integers
    /// <summary>8-bit unsigned integer literal (255u8)</summary>
    U8Literal,

    /// <summary>16-bit unsigned integer literal (65535u16)</summary>
    U16Literal,

    /// <summary>32-bit unsigned integer literal (42u32)</summary>
    U32Literal,

    /// <summary>64-bit unsigned integer literal (1000u64)</summary>
    U64Literal,

    /// <summary>128-bit unsigned integer literal (42u128)</summary>
    U128Literal,

    /// <summary>System pointer-sized unsigned integer literal (42usys)</summary>
    SysuintLiteral,

    // Floating point
    /// <summary>16-bit floating point literal (3.14f16)</summary>
    F16Literal,

    /// <summary>32-bit floating point literal (3.14f32)</summary>
    F32Literal,

    /// <summary>64-bit floating point literal (3.14f64)</summary>
    F64Literal,

    /// <summary>128-bit floating point literal (3.14f128)</summary>
    F128Literal,

    // Decimal
    /// <summary>32-bit decimal literal (3.14d32)</summary>
    D32Literal,

    /// <summary>64-bit decimal literal (3.14d64)</summary>
    D64Literal,

    /// <summary>128-bit decimal literal (3.14d128)</summary>
    D128Literal,

    // ===== MEMORY SIZE LITERALS =====
    /// <summary>Byte memory size literal (100b)</summary>
    ByteLiteral,

    // Kilobyte variants
    /// <summary>Kilobyte memory size literal using decimal (1000 bytes) (8kb)</summary>
    KilobyteLiteral,

    /// <summary>Kibibyte memory size literal using binary (1024 bytes) (8kib)</summary>
    KibibyteLiteral,

    /// <summary>Kilobit memory size literal using decimal (1000 bits) (8kbit)</summary>
    KilobitLiteral,

    /// <summary>Kibibit memory size literal using binary (1024 bits) (8kibit)</summary>
    KibibitLiteral,

    // Megabyte variants
    /// <summary>Megabyte memory size literal using decimal (1000² bytes) (100mb)</summary>
    MegabyteLiteral,

    /// <summary>Mebibyte memory size literal using binary (1024² bytes) (100mib)</summary>
    MebibyteLiteral,

    /// <summary>Megabit memory size literal using decimal (1000² bits) (100mbit)</summary>
    MegabitLiteral,

    /// <summary>Mebibit memory size literal using binary (1024² bits) (100mibit)</summary>
    MebibitLiteral,

    // Gigabyte variants
    /// <summary>Gigabyte memory size literal using decimal (1000³ bytes) (4gb)</summary>
    GigabyteLiteral,

    /// <summary>Gibibyte memory size literal using binary (1024³ bytes) (4gib)</summary>
    GibibyteLiteral,

    /// <summary>Gigabit memory size literal using decimal (1000³ bits) (4gbit)</summary>
    GigabitLiteral,

    /// <summary>Gibibit memory size literal using binary (1024³ bits) (4gibit)</summary>
    GibibitLiteral,

    // Terabyte variants
    /// <summary>Terabyte memory size literal using decimal (1000⁴ bytes) (1tb)</summary>
    TerabyteLiteral,

    /// <summary>Tebibyte memory size literal using binary (1024⁴ bytes) (1tib)</summary>
    TebibyteLiteral,

    /// <summary>Terabit memory size literal using decimal (1000⁴ bits) (1tbit)</summary>
    TerabitLiteral,

    /// <summary>Tebibit memory size literal using binary (1024⁴ bits) (1tibit)</summary>
    TebibitLiteral,

    // Petabyte variants
    /// <summary>Petabyte memory size literal using decimal (1000⁵ bytes) (5pb)</summary>
    PetabyteLiteral,

    /// <summary>Pebibyte memory size literal using binary (1024⁵ bytes) (5pib)</summary>
    PebibyteLiteral,

    /// <summary>Petabit memory size literal using decimal (1000⁵ bits) (5pbit)</summary>
    PetabitLiteral,

    /// <summary>Pebibit memory size literal using binary (1024⁵ bits) (5pibit)</summary>
    PebibitLiteral,

    // ===== DURATION LITERALS =====
    /// <summary>Week duration literal (2w)</summary>
    WeekLiteral,

    /// <summary>Day duration literal (30d)</summary>
    DayLiteral,

    /// <summary>Hour duration literal (24h)</summary>
    HourLiteral,

    /// <summary>Minute duration literal (30m)</summary>
    MinuteLiteral,

    /// <summary>Second duration literal (45s)</summary>
    SecondLiteral,

    /// <summary>Millisecond duration literal (500ms)</summary>
    MillisecondLiteral,

    /// <summary>Microsecond duration literal (100us)</summary>
    MicrosecondLiteral,

    /// <summary>Nanosecond duration literal (50ns)</summary>
    NanosecondLiteral,

    // ===== IDENTIFIERS =====
    /// <summary>Regular identifier in snake_case, may end with ! (my_var, is_valid!)</summary>
    Identifier,

    /// <summary>Type identifier in PascalCase (MyClass, HttpResponse)</summary>
    TypeIdentifier,

    // ===== KEYWORDS =====
    // Declaration keywords
    /// <summary>Function declaration keyword (recipe)</summary>
    recipe,

    /// <summary>Entity declaration keyword</summary>
    Entity,

    /// <summary>Record declaration keyword</summary>
    Record,

    /// <summary>Choice declaration keyword (enum in both languages)</summary>
    Choice,

    /// <summary>Chimera declaration keyword</summary>
    Chimera,

    /// <summary>Feature declaration keyword</summary>
    Protocol,

    /// <summary>Immutable variable declaration keyword</summary>
    Let,

    /// <summary>Mutable variable declaration keyword</summary>
    Var,

    /// <summary>Preset configuration keyword</summary>
    Preset,

    /// <summary>Private access modifier keyword</summary>
    Private,

    /// <summary>Public(family) access modifier keyword (`public(family)`)</summary>
    PublicFamily,

    /// <summary>Public(module) access modifier keyword (`public(module)`)</summary>
    PublicModule,

    /// <summary>Public access modifier keyword</summary>
    Public,

    /// <summary>External linkage modifier keyword</summary>
    External,

    /// <summary>Global scope modifier keyword</summary>
    Global,

    /// <summary>Static modifier keyword</summary>
    TypeWise,

    /// <summary>Self reference keyword</summary>
    Self,

    /// <summary>Super/parent reference keyword</summary>
    Super,

    /// <summary>From clause keyword for inheritance</summary>
    From,

    // Control flow keywords
    /// <summary>Conditional if statement keyword</summary>
    If,

    /// <summary>Else-if conditional keyword</summary>
    Elif,

    /// <summary>Else conditional keyword</summary>
    Else,

    /// <summary>One liner of conditional expression (if predicate then A else B)</summary>
    Then,

    /// <summary>Unless (negative if) conditional keyword</summary>
    Unless,

    /// <summary>Pattern matching when keyword</summary>
    When,

    /// <summary>Pattern matching is keyword</summary>
    Is,

    /// <summary>Infinite loop keyword</summary>
    Loop,

    /// <summary>While loop keyword</summary>
    While,

    /// <summary>For loop keyword</summary>
    For,

    /// <summary>Break statement keyword</summary>
    Break,

    /// <summary>Continue statement keyword</summary>
    Continue,

    /// <summary>Return statement keyword</summary>
    Return,

    // Module system keywords
    /// <summary>Import statement keyword</summary>
    Import,

    // Special keywords
    /// <summary>Using statement keyword</summary>
    Using,

    /// <summary>Type alias as keyword</summary>
    As,

    /// <summary>Method redefinition keyword (redefinition A = B)</summary>
    Define,

    /// <summary>No-operation pass keyword</summary>
    Pass,

    /// <summary>Bitter mode keyword for low-level operations</summary>
    Bitter,

    /// <summary>Danger mode keyword for unsafe operations</summary>
    Danger,

    /// <summary>Mayhem mode keyword for ultimate unsafe operations (method replacement, code execution)</summary>
    Mayhem,

    /// <summary>With clause keyword for context</summary>
    With,

    /// <summary>Where clause keyword for constraints</summary>
    Where,

    /// <summary>Follows keyword for ordering constraints</summary>
    Follows,

    /// <summary>In keyword for iterating/contains(ex, for i in 1 to 10 step 2)</summary>
    In,

    NotIn,
    IsNot,
    NotFrom,
    NotFollows,

    /// <summary>To keyword for Range object(ex, for i in 1 to 10 step 2)</summary>
    To,

    /// <summary>Step keyword for Range object(ex, for i in 1 to 10 step 2)</summary>
    Step,

    // Logical keywords
    /// <summary>Logical AND operator keyword</summary>
    And,

    /// <summary>Logical OR operator keyword</summary>
    Or,

    /// <summary>Logical NOT operator keyword</summary>
    Not,

    // Literal keywords
    /// <summary>Boolean true literal keyword</summary>
    True,

    /// <summary>Boolean false literal keyword</summary>
    False,

    /// <summary>Null/none literal keyword</summary>
    None,

    // ===== OPERATORS =====
    // Basic arithmetic
    /// <summary>Addition operator (+)</summary>
    Plus,

    /// <summary>Subtraction or unary minus operator (-)</summary>
    Minus,

    /// <summary>Multiplication or dereference operator (*)</summary>
    Star,

    /// <summary>Regular division operator (/)</summary>
    Slash,

    /// <summary>Modulo operator (%)</summary>
    Percent,

    /// <summary>Integer division operator (//)</summary>
    Divide,

    // Overflow arithmetic operators
    /// <summary>Wrapping addition operator (+%)</summary>
    PlusWrap,

    /// <summary>Saturating addition operator (+^)</summary>
    PlusSaturate,

    /// <summary>Unchecked addition operator (+!)</summary>
    PlusUnchecked,

    /// <summary>Checked addition operator (+?)</summary>
    PlusChecked,

    /// <summary>Wrapping subtraction operator (-%)</summary>
    MinusWrap,

    /// <summary>Saturating subtraction operator (-^)</summary>
    MinusSaturate,

    /// <summary>Unchecked subtraction operator (-!)</summary>
    MinusUnchecked,

    /// <summary>Checked subtraction operator (-?)</summary>
    MinusChecked,

    /// <summary>Wrapping multiplication operator (*%)</summary>
    MultiplyWrap,

    /// <summary>Saturating multiplication operator (*^)</summary>
    MultiplySaturate,

    /// <summary>Unchecked multiplication operator (*!)</summary>
    MultiplyUnchecked,

    /// <summary>Checked multiplication operator (*?)</summary>
    MultiplyChecked,

    /// <summary>Wrapping integer division operator (//%)</summary>
    DivideWrap,

    /// <summary>Saturating integer division operator (//^)</summary>
    DivideSaturate,

    /// <summary>Unchecked integer division operator (//!)</summary>
    DivideUnchecked,

    /// <summary>Checked integer division operator (//?)</summary>
    DivideChecked,

    /// <summary>Wrapping modulo operator (%%)</summary>
    ModuloWrap,

    /// <summary>Saturating modulo operator (%^)</summary>
    ModuloSaturate,

    /// <summary>Unchecked modulo operator (%!)</summary>
    ModuloUnchecked,

    /// <summary>Checked modulo operator (%?)</summary>
    ModuloChecked,

    /// <summary>Exponentiation operator (**)</summary>
    Power,

    /// <summary>Wrapping exponentiation operator (**%)</summary>
    PowerWrap,

    /// <summary>Saturating exponentiation operator (**^)</summary>
    PowerSaturate,

    /// <summary>Unchecked exponentiation operator (**!)</summary>
    PowerUnchecked,

    /// <summary>Checked exponentiation operator (**?)</summary>
    PowerChecked,

    // Comparison operators
    /// <summary>Equality comparison operator (==)</summary>
    Equal,

    /// <summary>Inequality comparison operator (!=)</summary>
    NotEqual,

    /// <summary>Less than comparison operator (<)</summary>
    Less,

    /// <summary>Less than or equal comparison operator (<=)</summary>
    LessEqual,

    /// <summary>Greater than comparison operator (>)</summary>
    Greater,

    /// <summary>Greater than or equal comparison operator (>=)</summary>
    GreaterEqual,

    // Bitwise operators
    /// <summary>Bitwise AND or reference operator (&)</summary>
    Ampersand,

    /// <summary>Bitwise OR or union operator (|)</summary>
    Pipe,

    /// <summary>Bitwise XOR operator (^)</summary>
    Caret,

    /// <summary>Bitwise NOT operator (~)</summary>
    Tilde,

    /// <summary>Left bit shift operator (<<)</summary>
    LeftShift,

    /// <summary>Right bit shift operator (>>)</summary>
    RightShift,

    // Assignment operators
    /// <summary>Assignment operator (=)</summary>
    Assign,

    // Special operators
    /// <summary>Negation or macro operator (!)</summary>
    Bang,

    /// <summary>Optional or ternary operator (?)</summary>
    Question,

    /// <summary>Attribute or annotation operator (@)</summary>
    At,

    /// <summary>Comment or preprocessing operator (#)</summary>
    Hash,

    /// <summary>Function arrow operator (->)</summary>
    Arrow,

    /// <summary>Match/lambda fat arrow operator (=>)</summary>
    FatArrow,

    /// <summary>Ternary conditional operator (?:)</summary>
    QuestionColon,

    // ===== DELIMITERS =====
    // Brackets and braces
    /// <summary>Left parenthesis delimiter (</summary>
    LeftParen,

    /// <summary>Right parenthesis delimiter )</summary>
    RightParen,

    /// <summary>Left square bracket delimiter [</summary>
    LeftBracket,

    /// <summary>Right square bracket delimiter ]</summary>
    RightBracket,

    /// <summary>Left curly brace delimiter {</summary>
    LeftBrace,

    /// <summary>Right curly brace delimiter }</summary>
    RightBrace,

    // Punctuation
    /// <summary>Member access dot operator (.)</summary>
    Dot,

    /// <summary>Comma separator (,)</summary>
    Comma,

    /// <summary>Type annotation colon (:)</summary>
    Colon,

    /// <summary>Namespace or static access operator (::)</summary>
    DoubleColon,

    /// <summary>Range operator (..)  </summary>
    DotDot,

    /// <summary>Spread or rest operator (...)</summary>
    DotDotDot,

    // ===== SPECIAL TOKENS =====
    /// <summary>Significant newline token (statement terminator)</summary>
    Newline,

    /// <summary>Indentation increase token (Cake language only)</summary>
    Indent,

    /// <summary>Indentation decrease token (Cake language only)</summary>
    Dedent,

    /// <summary>Documentation comment token (##)</summary>
    DocComment,

    /// <summary>requires token</summary>
    Requires,

    Generate,
    Suspended,
    Waitfor,
    Usurping,

    /// <summary>End of file marker token</summary>
    Eof,

    /// <summary>Unknown or invalid token</summary>
    Unknown
}
