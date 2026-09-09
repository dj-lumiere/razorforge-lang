namespace Compiler.Tokenizer;

#region Token Type Enumeration

/// <summary>
/// Defines all possible token types that can be produced by the lexical analyzer.
/// Each token represents a meaningful unit of source code that the parser can work with.
/// </summary>
/// <remarks>
/// This comprehensive enumeration covers:
/// <list type="bullet">
/// <item>Basic literals (integers, decimals, characters, strings)</item>
/// <item>Typed numeric literals with explicit bit widths</item>
/// <item>ByteSize literals (bytes, kilobytes, etc.)</item>
/// <item>Duration literals (seconds, milliseconds, etc.)</item>
/// <item>Keywords for declarations, control flow, and special operations</item>
/// <item>Operators (arithmetic, comparison, bitwise, assignment)</item>
/// <item>Delimiters and punctuation</item>
/// <item>Special tokens (indent, dedent, EOF)</item>
/// </list>
/// </remarks>
public enum TokenType
{
    #region Literals

    #region Basic Literals

    /// <summary>
    /// Undecided integer literal.
    /// </summary>
    UndecidedInteger,

    /// <summary>
    /// Undecided decimal literal.
    /// </summary>
    UndecidedDecimal,

    /// <summary>Byte letter literal with prefix (b'a')</summary>
    ByteLetterLiteral,

    /// <summary>Single character literal - default for plain quotes ('a', '\n')</summary>
    CharacterLiteral,

    #endregion

    #region Text Literals

    // Basic 32-bit texts (default)
    /// <summary>Text literal - default for plain quotes ("hello world")</summary>
    TextLiteral,

    /// <summary>Formatted text with inserting expressions (f"hello {name}")</summary>
    FormattedText,

    /// <summary>Raw text that doesn't process escape sequences (r"C:\path\file")</summary>
    RawText,

    /// <summary>Raw formatted text combining raw and inserting (rf"path: {dir}\file")</summary>
    RawFormattedText,

    /// <summary>Marks the beginning of an f-string (f" or rf")</summary>
    InsertionStart,

    /// <summary>Marks the end of an f-string (closing ")</summary>
    InsertionEnd,

    /// <summary>Literal text portion within an f-string</summary>
    TextSegment,

    /// <summary>Format specifier after : in {expr:spec}</summary>
    FormatSpec,

    /// <summary>Bytes literal with explicit prefix (b"hello")</summary>
    BytesLiteral,

    /// <summary>Bytes raw literal with explicit prefix (br"C:\path")</summary>
    BytesRawLiteral,

    #endregion

    #region Typed Numeric Literals

    // Signed integers
    /// <summary>8-bit signed integer literal (42s8)</summary>
    S8Literal,

    /// <summary>16-bit signed integer literal (1000s16)</summary>
    S16Literal,

    /// <summary>32-bit signed integer literal (42s32)</summary>
    S32Literal,

    /// <summary>64-bit signed integer literal (1000s64)</summary>
    S64Literal,

    /// <summary>128-bit signed integer literal (42s128)</summary>
    S128Literal,

    /// <summary>256-bit signed integer literal (42s256)</summary>
    S256Literal,

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

    /// <summary>256-bit unsigned integer literal (42u256)</summary>
    U256Literal,

    /// <summary>Arbitrary precision integer literal</summary>
    IntegerLiteral,

    /// <summary>Address-sized unsigned integer literal (42_addr)</summary>
    AddressLiteral,

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

    /// <summary>Arbitrary precision decimal literal</summary>
    DecimalLiteral,

    // Imaginary (for complex numbers)
    /// <summary>32-bit imaginary literal (4.0j32)</summary>
    J32Literal,

    /// <summary>64-bit imaginary literal (4.0j64 or 4.0j)</summary>
    J64Literal,

    /// <summary>128-bit imaginary literal (4.0j128)</summary>
    J128Literal,

    /// <summary>Arbitrary precision imaginary literal (4.0jn)</summary>
    JnLiteral,

    #endregion

    #region ByteSize Literals

    /// <summary>Byte ByteSize literal (100b)</summary>
    ByteLiteral,

    // Kilobyte variants
    /// <summary>Kilobyte ByteSize literal using decimal (1000 bytes) (8kb)</summary>
    KilobyteLiteral,

    /// <summary>Kibibyte ByteSize literal using binary (1024 bytes) (8kib)</summary>
    KibibyteLiteral,

    // Megabyte variants
    /// <summary>Megabyte ByteSize literal using decimal (1000² bytes) (100mb)</summary>
    MegabyteLiteral,

    /// <summary>Mebibyte ByteSize literal using binary (1024² bytes) (100mib)</summary>
    MebibyteLiteral,

    // Gigabyte variants
    /// <summary>Gigabyte ByteSize literal using decimal (1000³ bytes) (4gb)</summary>
    GigabyteLiteral,

    /// <summary>Gibibyte ByteSize literal using binary (1024³ bytes) (4gib)</summary>
    GibibyteLiteral,

    #endregion

    #region Duration Literals

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

    #endregion

    #endregion

    #region Identifiers

    /// <summary>Regular identifier prefixed with let/var/nothing</summary>
    Identifier,

    #endregion

    #region Keywords

    #region Keywords - Declarations

    #region Type Declarations

    /// <summary>
    /// Routine (function) declaration keyword.
    /// Defines a callable unit of code with optional parameters and return type.
    /// </summary>
    Routine,

    /// <summary>
    /// Entity declaration keyword.
    /// Reference type for complex, dynamically-sized objects with identity semantics.
    /// Accessed through the memory model (.view(), .modify(), .share(), steal).
    /// </summary>
    Entity,

    /// <summary>
    /// Record declaration keyword.
    /// Value type for simple, fixed-size data with value semantics. Copied on assignment.
    /// Use 'with' for modified copies (record with (memberVar: value)).
    /// </summary>
    Record,

    /// <summary>
    /// Choice declaration keyword.
    /// Simple enumeration of discrete, stateless options (type-safe enum).
    /// Perfect for pattern matching with 'when'.
    /// </summary>
    Choice,

    /// <summary>
    /// Flags declaration keyword.
    /// Bitwise flag set type - each member represents a power-of-two bit flag.
    /// Supports bitwise combination and pattern matching on flag combinations.
    /// </summary>
    Flags,

    /// <summary>
    /// Crashable declaration keyword.
    /// Entity type that can be thrown as an error. Must implement crash_message().
    /// Automatically conforms to the Crashable protocol.
    /// </summary>
    Crashable,

    /// <summary>
    /// Variant declaration keyword.
    /// Tagged union - each case can hold different data types.
    /// Needs pattern matching to unpack. Cases are immediately disposed after unpacking.
    /// </summary>
    Variant,

    /// <summary>
    /// Protocol declaration keyword.
    /// Interface/trait contract defining member routine signatures that types must implement.
    /// Types use 'obeys' keyword to implement protocols.
    /// </summary>
    Protocol,

    #endregion

    #region Variable Declarations

    /// <summary>Modifiable variable binding - value can be reassigned</summary>
    Var,

    /// <summary>
    /// Build-time constant declaration.
    /// Unmodifiable, build-time evaluated. Use SCREAMING_SNAKE_CASE by convention.
    /// </summary>
    Preset,

    /// <summary>
    /// Module-level mutable global declaration: `global counter: S64 = 0`.
    /// Module-scope storage, runtime-initialized. `global` already implies mutability,
    /// so no `var` — and module-level `var` is an error (use `global`).
    /// </summary>
    Global,

    /// <summary>
    /// Late-init modifier on var declarations: `lateinit var x: T`.
    /// Eager allocation, late initialization: the binding's storage is allocated
    /// (entities get a real zeroed heap block, create not run) so the value is
    /// immediately valid/accessible; reading before assignment yields meaningless
    /// (zeroed) values. Definite-assignment analysis is deferred to a later version.
    /// </summary>
    LateInit,

    #endregion

    #region Access Modifiers

    /// <summary>Secret access modifier - visible only within the declaring module</summary>
    Secret,

    /// <summary>Posted access modifier - open read, secret write</summary>
    Posted,

    /// <summary>Dangerous modifier - marks unsafe/dangerous routines or blocks</summary>
    Dangerous,

    /// <summary>
    /// Static/class-level routine modifier.
    /// No receiver (no 'me'), accessed via Type.MemberRoutine() syntax.
    /// </summary>
    Common,

    #endregion

    #region Self References

    /// <summary>Self reference keyword (me) - refers to the current instance in member routines</summary>
    Me,

    /// <summary>Self type keyword (Me) - the type of 'me', used in protocols for associated types</summary>
    MyType,

    #endregion

    #region Protocol Implementation

    /// <summary>
    /// Protocol implementation keyword.
    /// Declares that a type implements a protocol (record Foo obeys Bar).
    /// Similar to 'implements' in Java or ': Trait' in Rust.
    /// </summary>
    Obeys,

    /// <summary>
    /// Negated protocol constraint.
    /// Asserts a type does NOT implement a protocol (needs T disobeys SomeTrait).
    /// </summary>
    Disobeys,

    #endregion

    #endregion

    #region Keywords - Control Flow

    /// <summary>Conditional if statement keyword</summary>
    If,

    /// <summary>Elseif conditional keyword</summary>
    Elseif,

    /// <summary>Else conditional keyword</summary>
    Else,

    /// <summary>One-liner of conditional expression (if predicate then A else B)</summary>
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

    /// <summary>Each loop keyword</summary>
    Each,

    /// <summary>Comptime member-expansion loop keyword (`expand m in allmemvarof(T)`)</summary>
    Expand,

    /// <summary>Comptime member-variable reflection source keyword (`openmemvarof(T)`) — OPEN ∪ POSTED
    /// (publicly-readable) members only.</summary>
    OpenMemVarOf,

    /// <summary>Comptime member-variable reflection source keyword (`allmemvarof(T)`) — ALL members
    /// incl `secret`.</summary>
    AllMemVarOf,

    /// <summary>Comptime variant-arm reflection source keyword (`branchof(T)`)</summary>
    BranchOf,

    /// <summary>Comptime choice/flags-case reflection source keyword (`caseof(T)`)</summary>
    CaseOf,

    /// <summary>Standard-impl eligibility quantifier keyword (`needs P everywhere`)</summary>
    Everywhere,

    /// <summary>Break statement keyword</summary>
    Break,

    /// <summary>Continue statement keyword</summary>
    Continue,

    /// <summary>Return statement keyword</summary>
    Return,

    /// <summary>Throw statement keyword - recoverable failure (generates try_/check_/lookup_ variants)</summary>
    Throw,

    /// <summary>Pierce statement keyword - fatal, uncatchable crash (no variants, no `!`); pierces
    /// through every handler. The crash counterpart to the recoverable `throw`.</summary>
    Pierce,

    /// <summary>Absent statement keyword - indicates value not found (triggers Lookup generation)</summary>
    Absent,

    /// <summary>Becomes statement keyword (block result value)</summary>
    Becomes,

    #endregion

    #region Keywords - Module System

    /// <summary>Import statement keyword</summary>
    Import,

    /// <summary>Module declaration keyword (maps to 'module' keyword)</summary>
    Module,

    #endregion

    #region Keywords - Special

    /// <summary>Using statement keyword for resource management (using db.open() as conn)</summary>
    Using,

    /// <summary>Type alias/Resource management as keyword (type Foo as Bar, or 'as' in scoped access blocks)</summary>
    As,

    /// <summary>memberRoutine/Type redefinition keyword (define A as B)</summary>
    Define,

    /// <summary>No-operation pass keyword - placeholder for empty blocks</summary>
    Pass,

    /// <summary>Danger mode keyword for unsafe operations (danger! indented block)</summary>
    Danger,

    /// <summary>With clause keyword for record copying with modifications (a with .x: 42)</summary>
    With,

    /// <summary>Given clause keyword for lambda captures (x given a => x + a)</summary>
    Given,

    /// <summary>
    /// Ownership transfer keyword (RazorForge only).
    /// Transfers ownership and invalidates the source (becomes deadref).
    /// Used for: single entity transfer (steal node), container push (list.push(steal node)),
    /// and consuming iteration (each item in steal list).
    /// </summary>
    Steal,

    /// <summary>In keyword for iteration and containment (each i in list, x in set)</summary>
    In,

    /// <summary>Not in keyword - negated containment check (x notin set)</summary>
    NotIn,

    /// <summary>Is not keyword - negated type/pattern check (x isnot Type)</summary>
    IsNot,

    /// <summary>To keyword for ascending Range (each i in 1 to 10)</summary>
    To,

    /// <summary>Til keyword for exclusive range end (each i in 0 til 10 means [0, 10))</summary>
    Til,

    /// <summary>By keyword for Range step size (each i in 1 to 10 by 2)</summary>
    By,

    /// <summary>
    /// Discard keyword for explicit discarding of variable
    /// </summary>
    Discard,

    /// <summary>Threaded modifier - marks a routine to run on an OS thread (v0.1 concurrency)</summary>
    Threaded,

    /// <summary>Suspended modifier - marks a routine to run as a stackful coroutine (v0.2 async)</summary>
    Suspended,

    #endregion

    #region Keywords - Logical Operators

    /// <summary>Logical AND operator keyword</summary>
    And,

    /// <summary>Logical OR operator keyword</summary>
    Or,

    /// <summary>Logical NOT operator keyword</summary>
    Not,

    /// <summary>
    /// Flags removal / exclusion keyword.
    /// Removes flags from a value (perms but WRITE = bitwise AND NOT), or
    /// excludes flags in 'is' tests (perms is READ and WRITE but EXECUTE =
    /// READ and WRITE are set AND EXECUTE is NOT set).
    /// </summary>
    But,

    #endregion

    #region Keywords - Literals

    /// <summary>Boolean true literal keyword</summary>
    True,

    /// <summary>Boolean false literal keyword</summary>
    False,

    /// <summary>`None` keyword — the absent-arm *type* (used in patterns: `is None`, and as variant member declaration).</summary>
    None,

    /// <summary>`none` keyword — the absent *value* literal (only legal where the target type is Maybe[T] / Lookup[T] / Result[None] / a variant with a None arm).</summary>
    NoneValue,

    #endregion

    #endregion

    #region Operators

    #region Operators - Basic Arithmetic

    /// <summary>Addition operator (+)</summary>
    Plus,

    /// <summary>Subtraction or unary minus operator (-)</summary>
    Minus,

    /// <summary>Multiplication operator (*)</summary>
    Star,

    /// <summary>Regular division or module path separation operator (/)</summary>
    Slash,

    /// <summary>Integer division operator (//)</summary>
    Divide,

    /// <summary>Modulo operator (%)</summary>
    Percent,

    #endregion

    #region Operators - Overflow Variants

    /// <summary>Wrapping addition operator (+%)</summary>
    PlusWrap,

    /// <summary>Clamping addition operator (+^)</summary>
    PlusClamp,

    /// <summary>Wrapping subtraction operator (-%)</summary>
    MinusWrap,

    /// <summary>Clamping subtraction operator (-^)</summary>
    MinusClamp,

    /// <summary>Wrapping multiplication operator (*%)</summary>
    MultiplyWrap,

    /// <summary>Clamping multiplication operator (*^)</summary>
    MultiplyClamp,

    /// <summary>Exponentiation operator (**)</summary>
    Power,

    /// <summary>Wrapping exponentiation operator (**%)</summary>
    PowerWrap,

    /// <summary>Clamping exponentiation operator (**^)</summary>
    PowerClamp,

    #endregion

    #region Operators - Unchecked Variants

    /// <summary>Unchecked addition operator (+!) — UB on overflow, enables nsw/nuw LLVM hint</summary>
    PlusUnchecked,

    /// <summary>Unchecked subtraction operator (-!) — UB on overflow, enables nsw/nuw LLVM hint</summary>
    MinusUnchecked,

    /// <summary>Unchecked multiplication operator (*!) — UB on overflow, enables nsw/nuw LLVM hint</summary>
    MultiplyUnchecked,

    /// <summary>Unchecked float division operator (/!) — fdiv fast, no NaN/inf check</summary>
    SlashUnchecked,

    /// <summary>Unchecked integer floor division operator (//!) — bare sdiv/udiv, UB on zero or overflow</summary>
    DivideUnchecked,

    /// <summary>Unchecked modulo operator (%!) — bare srem/urem, UB on zero</summary>
    PercentUnchecked,

    /// <summary>Unchecked exponentiation operator (**!) — UB on overflow</summary>
    PowerUnchecked,

    #endregion

    #region Operators - Comparison

    /// <summary>Equality comparison operator (==)</summary>
    Equal,

    /// <summary>Inequality comparison operator (!=)</summary>
    NotEqual,

    /// <summary>Reference-identity comparison operator (===) — "same object?", distinct from value `==`.</summary>
    IdentityEqual,

    /// <summary>Negated reference-identity comparison operator (!==) — "different object?".</summary>
    IdentityNotEqual,

    /// <summary>Less than comparison operator (&lt;)</summary>
    Less,

    /// <summary>Less than or equal comparison operator (&lt;=)</summary>
    LessEqual,

    /// <summary>Greater than comparison operator (>)</summary>
    Greater,

    /// <summary>Greater than or equal comparison operator (>=)</summary>
    GreaterEqual,

    /// <summary>Three-way comparison (spaceship) operator (&lt;=&gt;)</summary>
    ThreeWayComparison,

    #endregion

    #region Operators - Bitwise

    /// <summary>Bitwise AND operator (&amp;)</summary>
    Ampersand,

    /// <summary>Bitwise OR operator (|)</summary>
    Pipe,

    /// <summary>Bitwise XOR operator (^)</summary>
    Caret,

    /// <summary>Bitwise NOT operator (~)</summary>
    Tilde,

    /// <summary>Left bit shift operator (&lt;&lt;)</summary>
    LeftShift,

    /// <summary>Right bit shift operator (>>)</summary>
    RightShift,

    /// <summary>Logical left bit shift operator (&lt;&lt;&lt;)</summary>
    LogicalLeftShift,

    /// <summary>Logical right bit shift operator (>>>)</summary>
    LogicalRightShift,

    #endregion

    #region Operators - Assignment and Special

    /// <summary>Assignment operator (=)</summary>
    Assign,

    /// <summary>Addition assignment operator (+=)</summary>
    PlusAssign,

    /// <summary>Subtraction assignment operator (-=)</summary>
    MinusAssign,

    /// <summary>Multiplication assignment operator (*=)</summary>
    StarAssign,

    /// <summary>Division assignment operator (/=)</summary>
    SlashAssign,

    /// <summary>Modulo assignment operator (%=)</summary>
    PercentAssign,

    /// <summary>Bitwise AND assignment operator (&amp;=)</summary>
    AmpersandAssign,

    /// <summary>Bitwise OR assignment operator (|=)</summary>
    PipeAssign,

    /// <summary>Bitwise XOR assignment operator (^=)</summary>
    CaretAssign,

    /// <summary>Left shift assignment operator (&lt;&lt;=)</summary>
    LeftShiftAssign,

    /// <summary>Right shift assignment operator (&gt;&gt;=)</summary>
    RightShiftAssign,

    /// <summary>Logical left shift assignment operator (&lt;&lt;&lt;=)</summary>
    LogicalLeftShiftAssign,

    /// <summary>Logical right shift assignment operator (&gt;&gt;&gt;=)</summary>
    LogicalRightShiftAssign,

    /// <summary>Integer division assignment operator (//=)</summary>
    DivideAssign,

    /// <summary>Power assignment operator (**=)</summary>
    PowerAssign,

    /// <summary>Wrapping add assignment operator (+%=)</summary>
    PlusWrapAssign,

    /// <summary>Wrapping subtract assignment operator (-%=)</summary>
    MinusWrapAssign,

    /// <summary>Wrapping multiply assignment operator (*%=)</summary>
    MultiplyWrapAssign,

    /// <summary>Wrapping power assignment operator (**%=)</summary>
    PowerWrapAssign,

    /// <summary>Clamping add assignment operator (+^=)</summary>
    PlusClampAssign,

    /// <summary>Clamping subtract assignment operator (-^=)</summary>
    MinusClampAssign,

    /// <summary>Clamping multiply assignment operator (*^=)</summary>
    MultiplyClampAssign,

    /// <summary>Clamping division operator (/^)</summary>
    SlashClamp,

    /// <summary>Clamping division assignment operator (/^=)</summary>
    SlashClampAssign,

    /// <summary>Clamping power assignment operator (**^=)</summary>
    PowerClampAssign,

    /// <summary>None coalescing assignment operator (??=)</summary>
    NoneCoalesceAssign,

    /// <summary>Crashable routine suffix (!)</summary>
    Bang,

    /// <summary>Wired member-routine marker prefix ($) — e.g. create, store. Structural: the
    /// parser records it as RoutineInfo.IsWiredMemberRoutine and keeps the name bare.</summary>
    Dollar,

    /// <summary>Comptime splice open marker (`${`) — the balanced `}` is an ordinary RightBrace.
    /// Distinct from a bare `$` (wired marker) and a bare `{`. Opens a `${expr}` comptime splice.</summary>
    SpliceOpen,

    /// <summary>Force unwrap operator (!!)</summary>
    BangBang,

    /// <summary>Optional type operator (?)</summary>
    Question,

    /// <summary>Annotation operator (@)</summary>
    At,

    /// <summary>Comment operator (#)</summary>
    Hash,

    /// <summary>FreeRoutine arrow operator (->)</summary>
    Arrow,

    /// <summary>CheckAndAdvance/lambda fat arrow operator (=>)</summary>
    FatArrow,

    /// <summary>None coalescing operator (??)</summary>
    NoneCoalesce,

    #endregion

    #endregion

    #region Delimiters

    #region Delimiters - Brackets and Braces

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

    #endregion

    #region Delimiters - Punctuation

    /// <summary>Member access dot operator (.)</summary>
    Dot,

    /// <summary>Optional chaining operator (?.)</summary>
    QuestionDot,

    /// <summary>Comma separator (,)</summary>
    Comma,

    /// <summary>Type annotation colon (:)</summary>
    Colon,

    /// <summary>Realm-qualifier separator (::) — e.g. <c>RF::Core.List</c> references the RazorForge
    /// (RF-mode, bare) realm from a Suflae file. Only meaningful immediately after a realm tag (RF/SF).</summary>
    DoubleColon,

    /// <summary>Vararg declaration operator (...) (ex, routine foo(values...: S32) -> S32</summary>
    DotDotDot,

    #endregion

    #endregion

    #region Special Tokens

    #region Whitespace Tokens

    /// <summary>Significant newline token (statement terminator)</summary>
    Newline,

    /// <summary>Indentation increment token</summary>
    Indent,

    /// <summary>Indentation decrement token</summary>
    Dedent,

    /// <summary>Documentation comment token (###)</summary>
    DocComment,

    #endregion

    #region Generic Constraints

    /// <summary>
    /// Generic type constraint keyword.
    /// Enforces build-time protocol requirements on generic types.
    /// Example: routine sort[T]() needs T obeys Comparable
    /// </summary>
    Needs,

    /// <summary>
    /// The 'onlyif' keyword for CONDITIONAL conformance in an obeys clause: a generic type obeys a
    /// protocol only when the parenthesized conditions hold (comma = AND).
    /// Example: entity List[T] obeys Copyable onlyif (T obeys Copyable)
    /// </summary>
    OnlyIf,

    /// <summary>
    /// The 'relates' keyword for associated types on type declarations.
    /// Declares an associated-type slot on a protocol, or binds one on an implementer.
    /// Example: protocol Iterable[T] \n relates Iter obeys Iterator[T]
    /// Example: entity List[T] obeys Iterable[T] \n relates ListEmitter[T] as Iter
    /// </summary>
    Relates,

    #endregion

    #region Terminal Tokens

    /// <summary>End of file marker token</summary>
    Eof,

    /// <summary>Unknown or invalid token</summary>
    Unknown

    #endregion

    #endregion
}

#endregion
