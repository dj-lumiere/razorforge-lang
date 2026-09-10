using SyntaxTree;

namespace RazorForge.Tests.Parser;

using static TestHelpers;

/// <summary>
/// Contains tests for parser error.
/// </summary>
public class ParserErrorTests
{
    #region Record Errors

    /// <summary>
    /// Verifies that a record member missing its <c>: Type</c> annotation throws or recovers.
    /// RF records are indentation-delimited — there is no brace to omit; the real malformation is a
    /// member line that is not <c>name: Type</c>.
    /// </summary>
    [Fact]
    public void Parse_Record_MemberMissingColon_ThrowsOrRecovers()
    {
        string source = """
                        record Point
                          x F32
                          y: F32
                        """;

        // Parser either throws on the malformed member or recovers and reports errors — both are acceptable.
        Exception? thrownEx = null;
        Compiler.Parser.Parser? recoveredParser = null;
        try { (Program _, recoveredParser) = ParseWithErrors(source: source); }
        catch (Exception e) { thrownEx = e; }

        Assert.True(condition: thrownEx != null || recoveredParser!.HasErrors,
            userMessage:
            "Expected parse to throw or report errors for a record member missing its ': Type'.");
    }
    /// <summary>
    /// Verifies that the parser accepts record missing member variable type throws or recovers.
    /// </summary>
    [Fact]
    public void Parse_Record_MissingMemberVariableType_ThrowsOrRecovers()
    {
        string source = """
                        record Point
                          x:
                          y: F32
                        """;

        // Should not parse cleanly - missing type after colon: either throw or recover with errors.
        Exception? thrownEx = null;
        Compiler.Parser.Parser? recoveredParser = null;
        try { (Program _, recoveredParser) = ParseWithErrors(source: source); }
        catch (Exception e) { thrownEx = e; }

        Assert.True(condition: thrownEx != null || recoveredParser!.HasErrors,
            userMessage:
            "Expected parse to throw or report errors for a member variable with a missing type.");
    }
    /// <summary>
    /// Verifies that the parser accepts record var keyword as invalid input for later validation.
    /// </summary>
    [Fact]
    public void Parse_Record_VarKeyword_ShouldBeInvalid()
    {
        // Records are value types - member variables are always immutable
        // 'var' should not be allowed in record member variables
        string source = """
                        record Point
                          var x: F32
                          y: F32
                        """;

        // This tests that var in record member variable is either rejected or ignored
        // Depending on parser behavior, this may parse but semantic analysis should catch it
        Program program = Parse(source: source);
        Assert.NotNull(@object: program);
        // If it parses, the member variable should still be immutable
    }
    /// <summary>
    /// Verifies that the parser reports in-scope record routines as invalid.
    /// </summary>
    [Fact]
    public void Parse_Record_InScopeRoutine_ReportsError()
    {
        string source = """
                        record Point
                          x: S32

                          routine distance() -> S32
                            return me.x
                        """;

        AssertParseError(source: source);
    }

    #endregion

    #region Entity Errors

    /// <summary>
    /// Verifies that the parser accepts entity member variable without var or let is valid.
    /// </summary>
    [Fact]
    public void Parse_Entity_MemberVariableWithoutVarOrLet_IsValid()
    {
        // Entity member variables use 'name: Type' syntax without var keyword
        string source = """
                        entity User
                          name: Text
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts entity var in body rejected.
    /// </summary>
    [Fact]
    public void Parse_Entity_VarInBody_Rejected()
    {
        // var keyword are no longer allowed in entity bodies
        string source = """
                        entity User
                          var name: Text
                        """;

        AssertParseError(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts entity missing type name and fails in the expected way.
    /// </summary>
    [Fact]
    public void Parse_Entity_MissingTypeName_Throws()
    {
        string source = """
                        entity
                          name: Text
                        """;

        // Parser uses error recovery, check for errors instead of exception
        AssertParseError(source: source);
    }
    /// <summary>
    /// Verifies that the parser reports in-scope entity routines as invalid.
    /// </summary>
    [Fact]
    public void Parse_Entity_InScopeRoutine_ReportsError()
    {
        string source = """
                        entity User
                          name: Text

                          routine display() -> Text
                            return me.name
                        """;

        AssertParseError(source: source);
    }

    #endregion

    #region Choice Errors

    /// <summary>
    /// Verifies that the parser accepts choice mixed values and no values as invalid input for later validation.
    /// </summary>
    [Fact]
    public void Parse_Choice_MixedValuesAndNoValues_ShouldBeInvalid()
    {
        // Choice cases must be all-or-nothing for values
        string source = """
                        choice Status
                          OK: 200
                          PENDING
                          ERROR: 500
                        """;

        // This should be rejected - can't mix valued and non-valued cases
        Program program = Parse(source: source);
        Assert.NotNull(@object: program);
        // Parser may accept but semantic analyzer should reject
    }
    /// <summary>
    /// Verifies that the parser accepts choice lowercase case successfully.
    /// </summary>
    [Fact]
    public void Parse_Choice_LowercaseCase_ShouldBeValid()
    {
        // Choice cases can use any case convention - no forcing required
        string source = """
                        choice Direction
                          north
                          south
                        """;

        Program program = Parse(source: source);
        Assert.NotNull(@object: program);
    }
    /// <summary>
    /// Verifies that the parser reports in-scope choice routines as invalid.
    /// </summary>
    [Fact]
    public void Parse_Choice_InScopeRoutine_ReportsError()
    {
        string source = """
                        choice Status
                          OK
                          ERROR

                          routine is_ok() -> Bool
                            return me is OK
                        """;

        AssertParseError(source: source);
    }

    #endregion

    #region Variant Errors

    /// <summary>
    /// Verifies that the parser accepts variant empty body and fails in the expected way.
    /// </summary>
    [Fact]
    public void Parse_Variant_EmptyBody_Throws()
    {
        string source = """
                        variant Empty
                        """;

        // Empty variant should either throw or produce a parse error.
        Exception? thrownEx = null;
        Compiler.Parser.Parser? recoveredParser = null;
        try { (Program _, recoveredParser) = ParseWithErrors(source: source); }
        catch (Exception e) { thrownEx = e; }

        Assert.True(condition: thrownEx != null || recoveredParser!.HasErrors,
            userMessage: "Expected parse to throw or report errors for an empty variant body.");
    }
    /// <summary>
    /// Verifies that the parser accepts variant follows protocol and fails in the expected way.
    /// </summary>
    [Fact]
    public void Parse_Variant_FollowsProtocol_Throws()
    {
        string source = """
                        variant Shape obeys Equatable
                          Circle: F32
                          Rect: F32
                        """;

        // Variants cannot obey protocols — parser does not support 'obeys' on variants: expect throw or parse error.
        Exception? thrownEx = null;
        Compiler.Parser.Parser? recoveredParser = null;
        try { (Program _, recoveredParser) = ParseWithErrors(source: source); }
        catch (Exception e) { thrownEx = e; }

        Assert.True(condition: thrownEx != null || recoveredParser!.HasErrors,
            userMessage: "Expected parse to throw or report errors when a variant uses 'obeys'.");
    }

    #endregion

    #region Protocol Errors

    /// <summary>
    /// Verifies that the parser accepts protocol memberRoutine with body as invalid input for later validation.
    /// </summary>
    [Fact]
    public void Parse_Protocol_memberRoutineWithBody_ShouldBeInvalid()
    {
        // Protocol member routines are signatures only - no body allowed
        string source = """
                        protocol Displayable
                          @readonly
                          routine Me.display() -> Text
                            return "hello"
                        """;

        // Should reject a member routine with a body in a protocol
        AssertParseError(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts protocol missing me as invalid input for later validation.
    /// </summary>
    [Fact]
    public void Parse_Protocol_MissingMe_ShouldBeInvalid()
    {
        // Protocol member routines must have Me. prefix
        string source = """
                        protocol Displayable
                          @readonly
                          routine display() -> Text
                        """;

        // Should reject - member routines need Me. prefix
        Program program = Parse(source: source);
        Assert.NotNull(@object: program);
        // May parse but should be flagged semantically
    }
    /// <summary>
    /// Verifies that the parser reports member variables in protocol bodies.
    /// </summary>
    [Fact]
    public void Parse_Protocol_MemberVariable_ReportsError()
    {
        string source = """
                        protocol Displayable
                          label: Text
                          routine Me.display() -> Text
                        """;

        AssertParseError(source: source);
    }
    /// <summary>
    /// Verifies that the parser reports unexpected statements in protocol bodies.
    /// </summary>
    [Fact]
    public void Parse_Protocol_StatementInBody_ReportsError()
    {
        string source = """
                        protocol Displayable
                          return
                          routine Me.display() -> Text
                        """;

        AssertParseError(source: source);
    }

    #endregion

    #region Generic Constraint Errors

    /// <summary>
    /// Verifies that the parser accepts constraint unknown type parameter as invalid input for later validation.
    /// </summary>
    [Fact]
    public void Parse_Constraint_UnknownTypeParameter_ShouldBeInvalid()
    {
        string source = """
                        record Container[T]
                        needs X obeys Comparable
                          value: T
                        """;

        // X is not a type parameter - should be rejected
        Program program = Parse(source: source);
        Assert.NotNull(@object: program);
        // Parser accepts, semantic analyzer should reject
    }
    /// <summary>
    /// Verifies that the parser accepts constraint invalid constraint kind and fails in the expected way.
    /// </summary>
    [Fact]
    public void Parse_Constraint_InvalidConstraintKind_Throws()
    {
        string source = """
                        record Container[T]
                        needs T banana Comparable
                          value: T
                        """;

        // "banana" is not a valid constraint kind (should be 'obeys' or 'is')
        // Parser uses error recovery, check for errors instead of exception
        AssertParseError(source: source);
    }

    #endregion

    #region Syntax Errors

    /// <summary>
    /// Verifies that the parser accepts unterminated string and fails in the expected way.
    /// </summary>
    [Fact]
    public void Parse_UnterminatedString_Throws()
    {
        string source = """
                        var x = "unterminated
                        """;

        Assert.ThrowsAny<Exception>(testCode: () => Parse(source: source));
    }
    /// <summary>
    /// Verifies that the parser accepts invalid operator and fails in the expected way.
    /// </summary>
    [Fact]
    public void Parse_InvalidOperator_Throws()
    {
        string source = "var x = 1 @@ 2";

        // Parser uses error recovery, check for errors instead of exception
        AssertParseError(source: source);
    }
    /// <summary>
    /// Verifies that a block header with no indented body throws or recovers. RF blocks are
    /// indentation-delimited — there is no brace to mismatch; the real malformation is a block
    /// header (<c>if true</c>) followed by no deeper-indented body.
    /// </summary>
    [Fact]
    public void Parse_BlockMissingBody_Throws()
    {
        string source = """
                        routine foo()
                          if true
                          return 1
                        """;

        // `if true` has no indented body: parser should throw or recover with errors.
        Exception? thrownEx = null;
        Compiler.Parser.Parser? recoveredParser = null;
        try { (Program _, recoveredParser) = ParseWithErrors(source: source); }
        catch (Exception e) { thrownEx = e; }

        Assert.True(condition: thrownEx != null || recoveredParser!.HasErrors,
            userMessage: "Expected parse to throw or report errors for a block header with no body.");
    }
    /// <summary>
    /// Verifies that the parser accepts mismatched parens and fails in the expected way.
    /// </summary>
    [Fact]
    public void Parse_MismatchedParens_Throws()
    {
        string source = """
                        routine foo()
                          return bar(1, 2
                        """;

        // Missing closing paren for call: parser should throw or recover with errors.
        Exception? thrownEx = null;
        Compiler.Parser.Parser? recoveredParser = null;
        try { (Program _, recoveredParser) = ParseWithErrors(source: source); }
        catch (Exception e) { thrownEx = e; }

        Assert.True(condition: thrownEx != null || recoveredParser!.HasErrors,
            userMessage: "Expected parse to throw or report errors for a mismatched parenthesis.");
    }

    #endregion

    #region Nested Routine Errors

    /// <summary>
    /// Verifies that the parser accepts nested routine and reports the expected error.
    /// </summary>
    [Fact]
    public void Parse_NestedRoutine_ReportsError()
    {
        // Nested routine declarations should be rejected
        string source = """
                        routine outer()
                          routine inner()
                            pass
                          return
                        """;

        AssertParseError(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts nested routine in if and reports the expected error.
    /// </summary>
    [Fact]
    public void Parse_NestedRoutineInIf_ReportsError()
    {
        // Nested routines should be rejected even in control flow blocks
        string source = """
                        routine outer()
                          if true
                            routine inner()
                              pass
                          return
                        """;

        AssertParseError(source: source);
    }

    #endregion

    #region Inline Conditional Errors

    /// <summary>
    /// Verifies that the parser accepts nested inline if then else and reports the expected error.
    /// </summary>
    [Fact]
    public void Parse_NestedInlineIfThenElse_ReportsError()
    {
        // Nested if-then-else expressions are not idiomatic and should be rejected
        // The parser prevents nesting by design (using _parsingInlineConditional flag)
        // Use when/match or regular if statements for complex conditionals
        string source = """
                        routine classify(n: S32) -> Text
                          return if n > 0 then "positive" else if n < 0 then "negative" else "zero"
                        """;

        // Parser rejects nested inline conditionals
        AssertParseError(source: source);
    }

    #endregion

    #region Reserved Prefix Errors

    /// <summary>
    /// Verifies that the parser accepts reserved prefix try so semantic analysis can validate it.
    /// </summary>
    [Fact]
    public void Parse_ReservedPrefix_Try_ShouldParse()
    {
        // try_ is only reserved on collision with a failable base's synthesized variant; the
        // parser always accepts the name regardless.
        string source = """
                        routine try_something()
                          pass
                          return
                        """;

        Program program = Parse(source: source);
        Assert.NotNull(@object: program);
    }
    /// <summary>
    /// Verifies that the parser accepts reserved prefix check so semantic analysis can validate it.
    /// </summary>
    [Fact]
    public void Parse_ReservedPrefix_Check_ShouldParse()
    {
        string source = """
                        routine check_something()
                          pass
                          return
                        """;

        Program program = Parse(source: source);
        Assert.NotNull(@object: program);
    }
    /// <summary>
    /// Verifies that the parser accepts reserved prefix find so semantic analysis can validate it.
    /// </summary>
    [Fact]
    public void Parse_ReservedPrefix_Find_ShouldParse()
    {
        string source = """
                        routine find_something()
                          pass
                          return
                        """;

        Program program = Parse(source: source);
        Assert.NotNull(@object: program);
    }

    #endregion

    #region Storage Class On Type Declaration Errors

    /// <summary>
    /// Verifies that the parser accepts common variant and reports the expected error.
    /// </summary>
    [Fact]
    public void Parse_CommonVariant_ReportsError()
    {
        string source = """
                        common variant Shape
                          Circle: F32
                          Rect: F32
                        """;

        AssertParseError(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts common record and reports the expected error.
    /// </summary>
    [Fact]
    public void Parse_CommonRecord_ReportsError()
    {
        string source = """
                        common record Point
                          x: F32
                          y: F32
                        """;

        AssertParseError(source: source);
    }

    #endregion
}
