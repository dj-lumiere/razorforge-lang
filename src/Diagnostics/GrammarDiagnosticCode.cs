using TypeModel.Enums;

namespace Compiler.Diagnostics;

/// <summary>
/// Unified grammar diagnostic codes for both RazorForge (RF-G) and Suflae (SF-G).
/// Covers lexer, parser, and indentation errors.
/// </summary>
public enum GrammarDiagnosticCode
{
    // ═══════════════════════════════════════════════════════════════════════════
    // LEXER ERRORS (001 - 049)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>A string literal was opened with a quote character but never closed before the end of the line or file.</summary>
    UnterminatedString = 1,

    /// <summary>A character was encountered that is not valid in this position in the source language.</summary>
    InvalidCharacter = 3,

    /// <summary>A numeric literal is malformed (e.g., invalid digits for the given base).</summary>
    InvalidNumericLiteral = 4,

    /// <summary>An escape sequence (e.g., \x, \u) in a string or character literal is not recognized or is malformed.</summary>
    InvalidEscapeSequence = 5,

    /// <summary>A floating-point literal is malformed (e.g., missing digits after the decimal point).</summary>
    InvalidFloatLiteral = 6,

    /// <summary>A memory size literal (e.g., 64KB, 1GiB) is malformed or uses an unrecognized unit.</summary>
    InvalidMemoryLiteral = 7,

    /// <summary>A duration literal (e.g., 500ms, 2s) is malformed or uses an unrecognized unit.</summary>
    InvalidDurationLiteral = 8,

    // ═══════════════════════════════════════════════════════════════════════════
    // INDENTATION ERRORS (050 - 079)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>A dedent (return to previous indentation level) was expected but the indentation did not decrease.</summary>
    ExpectedDedent = 51,

    /// <summary>The indentation decreased to a level that does not match any open indentation level.</summary>
    UnexpectedDedent = 52,

    /// <summary>Two lines in the same block have different indentation widths, making the structure ambiguous.</summary>
    InconsistentIndentation = 53,

    /// <summary>A block mixes tab and space characters for indentation, which is not allowed.</summary>
    MixedTabsAndSpaces = 54,

    /// <summary>A colon at the end of a statement was not followed by an indented block on the next line.</summary>
    ExpectedIndentedBlock = 55,

    // ═══════════════════════════════════════════════════════════════════════════
    // EXPECTED TOKEN ERRORS (100 - 149)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>A closing brace '}' was expected but not found.</summary>
    ExpectedClosingBrace = 100,

    /// <summary>A closing parenthesis ')' was expected but not found.</summary>
    ExpectedClosingParen = 101,

    /// <summary>A closing bracket ']' was expected but not found.</summary>
    ExpectedClosingBracket = 102,

    /// <summary>A colon ':' was expected but not found.</summary>
    ExpectedColon = 104,

    /// <summary>A thin arrow '->' was expected but not found.</summary>
    ExpectedArrow = 105,

    /// <summary>A fat arrow '=>' was expected but not found.</summary>
    ExpectedFatArrow = 106,

    /// <summary>An equals sign '=' was expected but not found.</summary>
    ExpectedEquals = 107,

    /// <summary>A comma ',' was expected but not found.</summary>
    ExpectedComma = 108,

    /// <summary>A dot '.' was expected but not found.</summary>
    ExpectedDot = 109,

    /// <summary>An identifier was expected but a different token was found.</summary>
    ExpectedIdentifier = 110,

    /// <summary>An expression was expected but a non-expression token was found.</summary>
    ExpectedExpression = 112,

    /// <summary>A type expression was expected but a non-type token was found.</summary>
    ExpectedType = 113,

    /// <summary>A pattern expression (for when/match clauses) was expected but not found.</summary>
    ExpectedPattern = 114,

    /// <summary>A top-level declaration (routine, record, entity, etc.) was expected but not found.</summary>
    ExpectedDeclaration = 115,

    /// <summary>An annotation value expression was expected after the annotation name.</summary>
    ExpectedAnnotationValue = 116,

    /// <summary>A type argument inside generic brackets was expected but not found.</summary>
    ExpectedTypeArgument = 117,

    /// <summary>Either a colon or fat arrow was expected (e.g., in when clauses) but neither was found.</summary>
    ExpectedColonOrFatArrow = 118,

    /// <summary>An opening parenthesis '(' was expected but not found.</summary>
    ExpectedLeftParen = 119,

    /// <summary>The 'as' keyword was expected (e.g., in import aliasing or cast expressions) but not found.</summary>
    ExpectedAs = 121,

    /// <summary>The 'waitfor' keyword was expected after a 'dependencies' block but not found.</summary>
    ExpectedWaitforAfterDependencies = 122,

    // ═══════════════════════════════════════════════════════════════════════════
    // UNEXPECTED TOKEN ERRORS (150 - 199)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>A token appeared in a position where it is not syntactically valid.</summary>
    UnexpectedToken = 150,

    /// <summary>The end of the file was reached unexpectedly while parsing an incomplete construct.</summary>
    UnexpectedEof = 151,

    /// <summary>A newline was encountered in a position where the expression or statement was expected to continue on the same line.</summary>
    UnexpectedNewline = 152,

    // ═══════════════════════════════════════════════════════════════════════════
    // STRUCTURE ERRORS (200 - 249)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>A routine definition was found inside another routine body, which is not permitted.</summary>
    NestedRoutineNotAllowed = 200,

    /// <summary>A visibility modifier (open, secret, posted) was used without a following declaration.</summary>
    VisibilityWithoutDeclaration = 201,

    /// <summary>One or more attributes were specified without a following declaration to attach them to.</summary>
    AnnotationsWithoutDeclaration = 202,

    /// <summary>A declaration that is only valid at the top level appeared inside a type or routine body.</summary>
    InvalidDeclarationInBody = 203,

    /// <summary>A 'throw' statement was used without a following expression to throw.</summary>
    ThrowRequiresExpression = 204,

    /// <summary>The indentation after a block body did not return to the expected level.</summary>
    ExpectedDedentAfterBody = 205,

    /// <summary>A discard statement ('_') was used on a non-call expression; discards are only valid for call results.</summary>
    DiscardRequiresCall = 206,

    /// <summary>The number of variables in a tuple destructuring assignment does not match the tuple's member variable count.</summary>
    TupleDependencyCountMismatch = 207,

    /// <summary>A RazorForge-only language construct was used in a Suflae source file.</summary>
    RfOnlyConstruct = 210,

    // ═══════════════════════════════════════════════════════════════════════════
    // PATTERN ERRORS (250 - 299)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>A pattern expression is syntactically malformed or uses an unsupported form.</summary>
    InvalidPattern = 250,

    /// <summary>A pattern variable binding was expected (e.g., after 'is') but not found.</summary>
    ExpectedPatternBinding = 251,

    /// <summary>A dot '.' was expected between the type name and case name in a qualified pattern.</summary>
    ExpectedDotInQualifiedPattern = 252,

    // ═══════════════════════════════════════════════════════════════════════════
    // GENERIC CONSTRAINT ERRORS (300 - 349)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>A constraint references a type parameter name that was not declared in the generic parameter list.</summary>
    UndeclaredTypeParameter = 300,

    /// <summary>A constraint keyword was used that is not a recognized constraint kind (e.g., neither 'obeys', 'from', nor a type category).</summary>
    InvalidConstraintKind = 301,

    /// <summary>A type expression was expected after a constraint keyword but not found.</summary>
    ExpectedConstraintType = 302
}

/// <summary>
/// Provides formatting helpers for <see cref="GrammarDiagnosticCode"/>.
/// </summary>
public static class GrammarDiagnosticCodeExtensions
{
    /// <summary>
    /// Formats code as RF-Gnnn or SF-Gnnn depending on language.
    /// </summary>
    public static string ToCodeString(this GrammarDiagnosticCode code, Language language)
    {
        string prefix = language == Language.RazorForge
            ? "RF"
            : "SF";
        return $"{prefix}-G{(int)code:D3}";
    }
}
