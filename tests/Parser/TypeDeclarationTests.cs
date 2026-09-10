using SyntaxTree;

namespace RazorForge.Tests.Parser;

using static TestHelpers;

/// <summary>
/// Contains tests for type declaration.
/// </summary>
public class TypeDeclarationTests
{
    #region Record Tests

    /// <summary>
    /// Verifies that the parser accepts simple record with member variables.
    /// </summary>
    [Fact]
    public void Parse_SimpleRecord_WithMemberVariables()
    {
        string source = """
                        record Point
                          x: F32
                          y: F32
                        """;

        Program program = AssertParses(source: source);
        RecordDeclaration record = GetDeclaration<RecordDeclaration>(program: program);

        Assert.Equal(expected: "Point", actual: record.Name);
        Assert.Equal(expected: 2, actual: record.Members.Count);
    }
    /// <summary>
    /// Verifies that the parser accepts generic record.
    /// </summary>
    [Fact]
    public void Parse_GenericRecord()
    {
        string source = """
                        record Container[T]
                          value: T
                        """;

        Program program = AssertParses(source: source);
        RecordDeclaration record = GetDeclaration<RecordDeclaration>(program: program);

        Assert.Equal(expected: "Container", actual: record.Name);
        Assert.NotNull(@object: record.GenericParameters);
        Assert.Single(collection: record.GenericParameters);
        Assert.Equal(expected: "T", actual: record.GenericParameters[index: 0]);
    }
    /// <summary>
    /// Verifies that the parser accepts record with constraint.
    /// </summary>
    [Fact]
    public void Parse_Record_WithConstraint()
    {
        string source = """
                        record Wrapper[T]
                        needs T obeys Comparable
                          value: T
                        """;

        Program program = AssertParses(source: source);
        RecordDeclaration record = GetDeclaration<RecordDeclaration>(program: program);

        Assert.NotNull(@object: record.GenericConstraints);
        Assert.Single(collection: record.GenericConstraints);
        Assert.Equal(expected: "T", actual: record.GenericConstraints[index: 0].ParameterName);
    }
    /// <summary>
    /// Verifies that the parser accepts multiline obeys constraint list.
    /// </summary>
    [Fact]
    public void Parse_Record_WithConstraint_MultilineObeysProtocols()
    {
        string source = """
                        record Wrapper[T]
                        needs T obeys Protocol1,
                        Protocol2,
                        Protocol3
                          value: T
                        """;

        Program program = AssertParses(source: source);
        RecordDeclaration record = GetDeclaration<RecordDeclaration>(program: program);

        Assert.NotNull(@object: record.GenericConstraints);
        Assert.Single(collection: record.GenericConstraints);
        GenericConstraintDeclaration constraint = record.GenericConstraints[index: 0];
        Assert.Equal(expected: "T", actual: constraint.ParameterName);
        Assert.Equal(expected: 3, actual: constraint.ConstraintTypes!.Count);
        Assert.Equal(expected: "Protocol1", actual: constraint.ConstraintTypes[index: 0].Name);
        Assert.Equal(expected: "Protocol2", actual: constraint.ConstraintTypes[index: 1].Name);
        Assert.Equal(expected: "Protocol3", actual: constraint.ConstraintTypes[index: 2].Name);
    }
    /// <summary>
    /// Verifies comma-separated constraints over DIFFERENT type parameters on a record —
    /// <c>needs T obeys A, U obeys B</c> — yield two constraints (the second was dropped before).
    /// </summary>
    [Fact]
    public void Parse_Record_WithConstraint_CommaSeparatedDifferentParams()
    {
        string source = """
                        record Pair[T, U]
                        needs T obeys Equatable, U obeys Equatable
                          a: T
                          b: U
                        """;

        Program program = AssertParses(source: source);
        RecordDeclaration record = GetDeclaration<RecordDeclaration>(program: program);

        Assert.NotNull(@object: record.GenericConstraints);
        Assert.Equal(expected: 2, actual: record.GenericConstraints!.Count);
        Assert.Equal(expected: "T", actual: record.GenericConstraints[index: 0].ParameterName);
        Assert.Equal(expected: "U", actual: record.GenericConstraints[index: 1].ParameterName);
    }
    /// <summary>
    /// Verifies the same comma-separated multi-parameter constraint form on a FREE routine —
    /// <c>routine pair[T, U](...) needs T obeys A, U obeys B</c>. This used to fail with RF-G055.
    /// </summary>
    [Fact]
    public void Parse_FreeRoutine_WithConstraint_CommaSeparatedDifferentParams()
    {
        string source = """
                        routine pair[T, U](a: T, b: U) -> Bool
                        needs T obeys Equatable, U obeys Equatable
                          return true
                        """;

        Program program = AssertParses(source: source);
        RoutineDeclaration routine = GetDeclaration<RoutineDeclaration>(program: program);

        Assert.NotNull(@object: routine.GenericConstraints);
        Assert.Equal(expected: 2, actual: routine.GenericConstraints!.Count);
        Assert.Equal(expected: "T", actual: routine.GenericConstraints[index: 0].ParameterName);
        Assert.Equal(expected: "U", actual: routine.GenericConstraints[index: 1].ParameterName);
    }

    /// <summary>
    /// Verifies that the parser accepts record follows protocol.
    /// </summary>
    [Fact]
    public void Parse_Record_FollowsProtocol()
    {
        string source = """
                        record Version obeys Comparable
                          major: S32
                          minor: S32
                        """;

        Program program = AssertParses(source: source);
        RecordDeclaration record = GetDeclaration<RecordDeclaration>(program: program);

        Assert.Single(collection: record.Protocols);
    }

    /// <summary>
    /// Verifies that the parser accepts record follows multiple protocols across lines.
    /// </summary>
    [Fact]
    public void Parse_Record_FollowsMultipleProtocols_AcrossLines()
    {
        string source = """
                        record Address
                        obeys UnsignedIntegral, Ordered, ConstCompatible, WrappingAddable, WrappingSubtractable,
                        WrappingMultiplicable, FloorDivisible
                          pass
                        """;

        Program program = AssertParses(source: source);
        RecordDeclaration record = GetDeclaration<RecordDeclaration>(program: program);

        Assert.Equal(expected: 7, actual: record.Protocols.Count);
        Assert.Equal(expected: "UnsignedIntegral", actual: record.Protocols[index: 0].Name);
        Assert.Equal(expected: "Ordered", actual: record.Protocols[index: 1].Name);
        Assert.Equal(expected: "ConstCompatible", actual: record.Protocols[index: 2].Name);
        Assert.Equal(expected: "WrappingAddable", actual: record.Protocols[index: 3].Name);
        Assert.Equal(expected: "WrappingSubtractable", actual: record.Protocols[index: 4].Name);
        Assert.Equal(expected: "WrappingMultiplicable", actual: record.Protocols[index: 5].Name);
        Assert.Equal(expected: "FloorDivisible", actual: record.Protocols[index: 6].Name);
    }
    /// <summary>
    /// Verifies that the parser accepts record multiple type parameters.
    /// </summary>
    [Fact]
    public void Parse_Record_MultipleTypeParameters()
    {
        string source = """
                        record Pair[K, V]
                          key: K
                          value: V
                        """;

        Program program = AssertParses(source: source);
        RecordDeclaration record = GetDeclaration<RecordDeclaration>(program: program);

        Assert.NotNull(@object: record.GenericParameters);
        Assert.Equal(expected: 2, actual: record.GenericParameters.Count);
        Assert.Equal(expected: "K", actual: record.GenericParameters[index: 0]);
        Assert.Equal(expected: "V", actual: record.GenericParameters[index: 1]);
    }

    #endregion

    #region Entity Tests

    /// <summary>
    /// Verifies that the parser accepts simple entity.
    /// </summary>
    [Fact]
    public void Parse_SimpleEntity()
    {
        string source = """
                        entity User
                          name: Text
                          age: U32
                        """;

        Program program = AssertParses(source: source);
        EntityDeclaration entity = GetDeclaration<EntityDeclaration>(program: program);

        Assert.Equal(expected: "User", actual: entity.Name);
        Assert.Equal(expected: 2, actual: entity.Members.Count);
    }
    /// <summary>
    /// Verifies that the parser accepts generic entity.
    /// </summary>
    [Fact]
    public void Parse_GenericEntity()
    {
        string source = """
                        entity Stack[T]
                          items: List[T]
                        """;

        Program program = AssertParses(source: source);
        EntityDeclaration entity = GetDeclaration<EntityDeclaration>(program: program);

        Assert.NotNull(@object: entity.GenericParameters);
        Assert.Single(collection: entity.GenericParameters);
        Assert.Equal(expected: "T", actual: entity.GenericParameters[index: 0]);
    }
    /// <summary>
    /// Verifies that the parser accepts entity multiple constraints.
    /// </summary>
    [Fact]
    public void Parse_Entity_MultipleConstraints()
    {
        string source = """
                        entity SortedCache[K, V]
                        needs K obeys Comparable
                        needs K obeys Hashable
                        needs EntityType V
                          entries: Dict[K, V]
                        """;

        Program program = AssertParses(source: source);
        EntityDeclaration entity = GetDeclaration<EntityDeclaration>(program: program);

        Assert.NotNull(@object: entity.GenericParameters);
        Assert.Equal(expected: 2, actual: entity.GenericParameters.Count);
        Assert.NotNull(@object: entity.GenericConstraints);
        Assert.Equal(expected: 3, actual: entity.GenericConstraints.Count);
    }

    #endregion

    #region Choice Tests

    /// <summary>
    /// Verifies that the parser accepts simple choice.
    /// </summary>
    [Fact]
    public void Parse_SimpleChoice()
    {
        string source = """
                        choice Direction
                          NORTH
                          SOUTH
                          EAST
                          WEST
                        """;

        Program program = AssertParses(source: source);
        ChoiceDeclaration choice = GetDeclaration<ChoiceDeclaration>(program: program);

        Assert.Equal(expected: "Direction", actual: choice.Name);
        Assert.Equal(expected: 4, actual: choice.Cases.Count);
        Assert.Equal(expected: "NORTH", actual: choice.Cases[index: 0].Name);
    }
    /// <summary>
    /// Verifies that the parser accepts choice with values.
    /// </summary>
    [Fact]
    public void Parse_Choice_WithValues()
    {
        string source = """
                        choice HttpStatus
                          OK: 200
                          NOT_FOUND: 404
                          ERROR: 500
                        """;

        Program program = AssertParses(source: source);
        ChoiceDeclaration choice = GetDeclaration<ChoiceDeclaration>(program: program);

        Assert.Equal(expected: 3, actual: choice.Cases.Count);
        Assert.NotNull(@object: choice.Cases[index: 0].Value);
    }

    #endregion

    #region Variant Tests

    /// <summary>
    /// Verifies that the parser accepts simple variant.
    /// </summary>
    [Fact]
    public void Parse_SimpleVariant()
    {
        string source = """
                        variant NetworkEvent
                          S32
                          Text
                        """;

        Program program = AssertParses(source: source);
        VariantDeclaration variant = GetDeclaration<VariantDeclaration>(program: program);

        Assert.Equal(expected: "NetworkEvent", actual: variant.Name);
        Assert.Equal(expected: 2, actual: variant.Members.Count);
    }
    /// <summary>
    /// Verifies that the parser accepts variant with types.
    /// </summary>
    [Fact]
    public void Parse_Variant_WithTypes()
    {
        string source = """
                        variant ParseResult
                          S32
                          Text
                        """;

        Program program = AssertParses(source: source);
        VariantDeclaration variant = GetDeclaration<VariantDeclaration>(program: program);

        Assert.Equal(expected: 2, actual: variant.Members.Count);
        Assert.Equal(expected: "S32", actual: variant.Members[index: 0].Type.Name);
        Assert.Equal(expected: "Text", actual: variant.Members[index: 1].Type.Name);
    }
    /// <summary>
    /// Verifies that the parser accepts variant with none.
    /// </summary>
    [Fact]
    public void Parse_Variant_WithNone()
    {
        string source = """
                        variant Event
                          S32
                          Text
                          None
                        """;

        Program program = AssertParses(source: source);
        VariantDeclaration variant = GetDeclaration<VariantDeclaration>(program: program);

        Assert.Equal(expected: 3, actual: variant.Members.Count);
        Assert.Equal(expected: "S32", actual: variant.Members[index: 0].Type.Name);
        Assert.Equal(expected: "Text", actual: variant.Members[index: 1].Type.Name);
        Assert.Equal(expected: "None", actual: variant.Members[index: 2].Type.Name);
    }

    #endregion

    #region Protocol Tests

    /// <summary>
    /// Verifies that the parser accepts simple protocol.
    /// </summary>
    [Fact]
    public void Parse_SimpleProtocol()
    {
        string source = """
                        protocol Displayable
                          @readonly
                          routine Me.display() -> Text
                        """;

        Program program = AssertParses(source: source);
        ProtocolDeclaration protocol = GetDeclaration<ProtocolDeclaration>(program: program);

        Assert.Equal(expected: "Displayable", actual: protocol.Name);
        Assert.Single(collection: protocol.MemberRoutines);
    }
    /// <summary>
    /// Verifies that the parser accepts protocol multiple memberRoutines.
    /// </summary>
    [Fact]
    public void Parse_Protocol_MultipleMemberRoutines()
    {
        string source = """
                        protocol Container
                          @readonly
                          routine Me.count() -> uaddr

                          @readonly
                          routine Me.is_empty() -> bool
                        """;

        Program program = AssertParses(source: source);
        ProtocolDeclaration protocol = GetDeclaration<ProtocolDeclaration>(program: program);

        Assert.Equal(expected: 2, actual: protocol.MemberRoutines.Count);
    }
    /// <summary>
    /// Verifies that the parser accepts generic protocol.
    /// </summary>
    [Fact]
    public void Parse_GenericProtocol()
    {
        string source = """
                        protocol Iterable[T]
                          @readonly
                          routine Me.iterate() -> Iterator[T]
                        """;

        Program program = AssertParses(source: source);
        ProtocolDeclaration protocol = GetDeclaration<ProtocolDeclaration>(program: program);

        Assert.NotNull(@object: protocol.GenericParameters);
        Assert.Single(collection: protocol.GenericParameters);
        Assert.Equal(expected: "T", actual: protocol.GenericParameters[index: 0]);
    }
    /// <summary>
    /// Verifies that the parser accepts protocol inheritance.
    /// </summary>
    [Fact]
    public void Parse_Protocol_Inheritance()
    {
        string source = """
                        protocol Ordered obeys Comparable
                          @readonly
                          routine Me.$cmp(other: Me) -> ComparisonSign
                        """;

        Program program = AssertParses(source: source);
        ProtocolDeclaration protocol = GetDeclaration<ProtocolDeclaration>(program: program);

        Assert.Single(collection: protocol.ParentProtocols);
    }

    #endregion

    #region Visibility Tests

    /// <summary>
    /// Verifies that the parser accepts secret record.
    /// </summary>
    [Fact]
    public void Parse_SecretRecord()
    {
        string source = """
                        secret record InternalData
                          value: S32
                        """;

        Program program = AssertParses(source: source);
        RecordDeclaration record = GetDeclaration<RecordDeclaration>(program: program);

        Assert.Equal(expected: VisibilityModifier.Secret, actual: record.Visibility);
    }
    /// <summary>
    /// Verifies that the parser accepts secret entity.
    /// </summary>
    [Fact]
    public void Parse_SecretEntity()
    {
        string source = """
                        secret entity CacheEntry
                          data: Text
                        """;

        Program program = AssertParses(source: source);
        EntityDeclaration entity = GetDeclaration<EntityDeclaration>(program: program);

        Assert.Equal(expected: VisibilityModifier.Secret, actual: entity.Visibility);
    }

    #endregion

    #region Routine Tests

    /// <summary>
    /// Verifies that the parser accepts simple routine.
    /// </summary>
    [Fact]
    public void Parse_SimpleRoutine()
    {
        string source = """
                        routine greet(name: Text) -> Text
                          return name
                        """;

        Program program = AssertParses(source: source);
        RoutineDeclaration routine = GetDeclaration<RoutineDeclaration>(program: program);

        Assert.Equal(expected: "greet", actual: routine.Name);
        Assert.Single(collection: routine.Parameters);
    }
    /// <summary>
    /// Verifies that the parser accepts failable routine.
    /// </summary>
    [Fact]
    public void Parse_FailableRoutine()
    {
        string source = """
                        routine get_value!() -> S32
                          return 42
                        """;

        Program program = AssertParses(source: source);
        RoutineDeclaration routine = GetDeclaration<RoutineDeclaration>(program: program);

        Assert.Equal(expected: "get_value", actual: routine.Name);
        Assert.True(condition: routine.IsFailable);
    }

    #endregion

    #region Posted Record Field Tests

    /// <summary>
    /// Verifies that the parser accepts record with posted member variable.
    /// </summary>
    [Fact]
    public void Parse_RecordWithPostedMemberVariable()
    {
        string source = """
                        record Percentage
                          posted value: F64
                        """;

        Program program = AssertParses(source: source);
        RecordDeclaration record = GetDeclaration<RecordDeclaration>(program: program);

        Assert.Equal(expected: "Percentage", actual: record.Name);
        Assert.Single(collection: record.Members);
    }
    /// <summary>
    /// Verifies that the parser accepts record with mixed visibility member variables.
    /// </summary>
    [Fact]
    public void Parse_RecordWithMixedVisibilityMemberVariables()
    {
        string source = """
                        record Config
                          posted name: Text
                          secret hidden: Text
                          value: S32
                        """;

        Program program = AssertParses(source: source);
        RecordDeclaration record = GetDeclaration<RecordDeclaration>(program: program);

        Assert.Equal(expected: 3, actual: record.Members.Count);
    }

    #endregion

    #region Flags Tests

    /// <summary>
    /// Verifies that the parser accepts simple flags declaration.
    /// </summary>
    [Fact]
    public void Parse_SimpleFlags()
    {
        string source = """
                        flags Permission
                          READ
                          WRITE
                          EXECUTE
                        """;

        Program program = AssertParses(source: source);
        FlagsDeclaration flags = GetDeclaration<FlagsDeclaration>(program: program);

        Assert.Equal(expected: "Permission", actual: flags.Name);
        Assert.Equal(expected: 3, actual: flags.Members.Count);
        Assert.Equal(expected: "READ", actual: flags.Members[index: 0]);
        Assert.Equal(expected: "WRITE", actual: flags.Members[index: 1]);
        Assert.Equal(expected: "EXECUTE", actual: flags.Members[index: 2]);
    }

    /// <summary>
    /// Verifies that the parser accepts flags with a single member.
    /// </summary>
    [Fact]
    public void Parse_Flags_SingleMember()
    {
        string source = """
                        flags OnOff
                          ON
                        """;

        Program program = AssertParses(source: source);
        FlagsDeclaration flags = GetDeclaration<FlagsDeclaration>(program: program);

        Assert.Equal(expected: "OnOff", actual: flags.Name);
        Assert.Single(collection: flags.Members);
        Assert.Equal(expected: "ON", actual: flags.Members[index: 0]);
    }

    /// <summary>
    /// Verifies that the parser accepts flags with secret visibility.
    /// </summary>
    [Fact]
    public void Parse_Flags_WithSecretVisibility()
    {
        string source = """
                        secret flags InternalMode
                          FAST
                          SAFE
                        """;

        Program program = AssertParses(source: source);
        FlagsDeclaration flags = GetDeclaration<FlagsDeclaration>(program: program);

        Assert.Equal(expected: "InternalMode", actual: flags.Name);
        Assert.Equal(expected: VisibilityModifier.Secret, actual: flags.Visibility);
        Assert.Equal(expected: 2, actual: flags.Members.Count);
    }

    #endregion

    #region Crashable Tests

    /// <summary>
    /// Verifies that the parser accepts simple crashable with member fields.
    /// </summary>
    [Fact]
    public void Parse_SimpleCrashable()
    {
        string source = """
                        crashable FileError
                          path: Text
                          reason: Text
                        """;

        Program program = AssertParses(source: source);
        CrashableDeclaration crashable = GetDeclaration<CrashableDeclaration>(program: program);

        Assert.Equal(expected: "FileError", actual: crashable.Name);
        Assert.Equal(expected: 2, actual: crashable.Members.Count);
    }

    /// <summary>
    /// Verifies that the parser accepts crashable with a single member field.
    /// </summary>
    [Fact]
    public void Parse_Crashable_SingleField()
    {
        string source = """
                        crashable ParseError
                          message: Text
                        """;

        Program program = AssertParses(source: source);
        CrashableDeclaration crashable = GetDeclaration<CrashableDeclaration>(program: program);

        Assert.Equal(expected: "ParseError", actual: crashable.Name);
        Assert.Single(collection: crashable.Members);
    }

    /// <summary>
    /// Verifies that the parser accepts crashable with secret visibility.
    /// </summary>
    [Fact]
    public void Parse_Crashable_WithSecretVisibility()
    {
        string source = """
                        secret crashable InternalError
                          code: S32
                        """;

        Program program = AssertParses(source: source);
        CrashableDeclaration crashable = GetDeclaration<CrashableDeclaration>(program: program);

        Assert.Equal(expected: "InternalError", actual: crashable.Name);
        Assert.Equal(expected: VisibilityModifier.Secret, actual: crashable.Visibility);
    }

    #endregion
}
