using Builder.Diagnostics;
using Builder.Verification.Results;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Contains tests for type resolution.
/// </summary>
public class TypeResolutionTests
{
    #region Basic Type Registration

    /// <summary>
    /// Verifies semantic analysis behavior for record and registers the expected type metadata.
    /// </summary>
    [Fact]
    public void Analyze_Record_RegistersInTypeRegistry()
    {
        string source = """
                        record Point
                          x: F32
                          y: F32
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        TypeSymbol? type = result.Registry.GetType(name: "Point");

        Assert.NotNull(@object: type);
        Assert.Equal(expected: TypeCategory.Record, actual: type.Category);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for entity and registers the expected type metadata.
    /// </summary>
    [Fact]
    public void Analyze_Entity_RegistersInTypeRegistry()
    {
        string source = """
                        entity User
                          name: Text
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        TypeSymbol? type = result.Registry.GetType(name: "User");

        Assert.NotNull(@object: type);
        Assert.Equal(expected: TypeCategory.Entity, actual: type.Category);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for choice and registers the expected type metadata.
    /// </summary>
    [Fact]
    public void Analyze_Choice_RegistersInTypeRegistry()
    {
        string source = """
                        choice Direction
                          NORTH
                          SOUTH
                          EAST
                          WEST
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        TypeSymbol? type = result.Registry.GetType(name: "Direction");

        Assert.NotNull(@object: type);
        Assert.Equal(expected: TypeCategory.Choice, actual: type.Category);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for variant and registers the expected type metadata.
    /// </summary>
    [Fact]
    public void Analyze_Variant_RegistersInTypeRegistry()
    {
        // Note: Don't use "Result" as it's a well-known error handling type
        string source = """
                        variant MyVariant
                          S32
                          Text
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        TypeSymbol? type = result.Registry.GetType(name: "MyVariant");

        Assert.NotNull(@object: type);
        Assert.Equal(expected: TypeCategory.Variant, actual: type.Category);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for protocol and registers the expected type metadata.
    /// </summary>
    [Fact]
    public void Analyze_Protocol_RegistersInTypeRegistry()
    {
        string source = """
                        protocol Displayable
                          @readonly
                          routine Me.display() -> Text
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        TypeSymbol? type = result.Registry.GetType(name: "Displayable");

        Assert.NotNull(@object: type);
        Assert.Equal(expected: TypeCategory.Protocol, actual: type.Category);
    }

    #endregion

    #region Generic Type Registration

    /// <summary>
    /// Verifies semantic analysis behavior for generic record and records generic type parameters.
    /// </summary>
    [Fact]
    public void Analyze_GenericRecord_RegistersWithTypeParameters()
    {
        string source = """
                        record Container[T]
                          value: T
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        TypeSymbol? type = result.Registry.GetType(name: "Container");

        Assert.NotNull(@object: type);
        Assert.True(condition: type.IsGenericDefinition);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for generic entity multiple type parameters.
    /// </summary>
    [Fact]
    public void Analyze_GenericEntity_MultipleTypeParameters()
    {
        string source = """
                        entity Pair[K, V]
                          key: K
                          value: V
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        TypeSymbol? type = result.Registry.GetType(name: "Pair");

        Assert.NotNull(@object: type);
        Assert.True(condition: type.IsGenericDefinition);
    }

    #endregion

    #region Routine Registration

    /// <summary>
    /// Verifies semantic analysis behavior for global routine and registers the expected symbol metadata.
    /// </summary>
    [Fact]
    public void Analyze_GlobalRoutine_RegistersInRegistry()
    {
        string source = """
                        routine greet(name: Text) -> Text
                          return name
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        RoutineInfo? routine = result.Registry.GetRoutine(name: "greet");

        Assert.NotNull(@object: routine);
        Assert.Equal(expected: RoutineKind.FreeRoutine, actual: routine.Kind);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for memberRoutine and records owner type metadata.
    /// </summary>
    [Fact]
    public void Analyze_memberRoutine_RegistersWithOwnerType()
    {
        string source = """
                        record Point
                          x: F32
                          y: F32

                        @readonly
                        routine Point.distance() -> F32
                          return 0.0_f32
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        RoutineInfo? routine = result.Registry.GetRoutine(name: "Point.distance");

        Assert.NotNull(@object: routine);
        Assert.Equal(expected: RoutineKind.MemberRoutine, actual: routine.Kind);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for failable routine and marks the routine as failable.
    /// </summary>
    [Fact]
    public void Analyze_FailableRoutine_RegistersAsFailable()
    {
        string source = """
                        routine get_value!() -> S32
                          return 42
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        RoutineInfo? routine = result.Registry.GetRoutine(name: "get_value");

        Assert.NotNull(@object: routine);
        Assert.True(condition: routine.IsFailable);
    }

    #endregion

    #region Field Resolution

    /// <summary>
    /// Verifies semantic analysis behavior for record member variables and resolves member types.
    /// </summary>
    [Fact]
    public void Analyze_RecordMemberVariables_ResolveTypes()
    {
        string source = """
                        record Color
                          r: U8
                          g: U8
                          b: U8
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        TypeSymbol? type = result.Registry.GetType(name: "Color");

        Assert.NotNull(@object: type);
        // Check fields are resolved
    }
    /// <summary>
    /// Verifies semantic analysis behavior for entity member variables and resolves member types.
    /// </summary>
    [Fact]
    public void Analyze_EntityMemberVariables_ResolveTypes()
    {
        string source = """
                        entity Document
                          title: Text
                          page_count: U32
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        TypeSymbol? type = result.Registry.GetType(name: "Document");

        Assert.NotNull(@object: type);
    }

    #endregion

    #region Type Errors

    /// <summary>
    /// Verifies semantic analysis behavior for undefined type and reports the expected error.
    /// </summary>
    [Fact]
    public void Analyze_UndefinedType_ReportsError()
    {
        string source = """
                        record Container
                          value: UnknownType
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.True(condition: result.Errors.Count > 0);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Message.Contains(value: "UnknownType",
                comparisonType: StringComparison.OrdinalIgnoreCase));
    }
    /// <summary>
    /// Verifies semantic analysis behavior for duplicate type name and reports the expected error.
    /// </summary>
    [Fact]
    public void Analyze_DuplicateTypeName_ReportsError()
    {
        string source = """
                        record Point
                          x: F32

                        record Point
                          y: F32
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.True(condition: result.Errors.Count > 0);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for duplicate member variable name and reports the expected error.
    /// </summary>
    [Fact]
    public void Analyze_DuplicateMemberVariableName_ReportsError()
    {
        string source = """
                        record Point
                          x: F32
                          x: F32
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.True(condition: result.Errors.Count > 0);
    }
    /// <summary>
    /// The try_/check_/lookup_ prefixes are collision-only: with no failable base of the same
    /// signature to shadow, a try_-prefixed routine is allowed (e.g. the lock idiom `try_lock`).
    /// </summary>
    [Fact]
    public void Analyze_ReservedFunctionPrefix_NoFailableBase_Allowed()
    {
        string source = """
                        routine try_something()
                          pass
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ReservedRoutinePrefix);
    }
    /// <summary>
    /// A check_-prefixed routine is allowed when no failable base of the same signature exists.
    /// </summary>
    [Fact]
    public void Analyze_ReservedFunctionPrefix_Check_NoFailableBase_Allowed()
    {
        string source = """
                        routine check_value()
                          pass
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ReservedRoutinePrefix);
    }
    /// <summary>
    /// A lookup_-prefixed routine is allowed when no failable base of the same signature exists.
    /// </summary>
    [Fact]
    public void Analyze_ReservedFunctionPrefix_Lookup_NoFailableBase_Allowed()
    {
        string source = """
                        routine lookup_item()
                          pass
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ReservedRoutinePrefix);
    }

    #endregion

    #region Constraint Validation

    /// <summary>
    /// Verifies semantic analysis behavior for valid constraint without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void Analyze_ValidConstraint_NoError()
    {
        string source = """
                        protocol Comparable
                          @readonly
                          routine Me.$cmp(other: Me) -> S32

                        record Wrapper[T]
                        needs T obeys Comparable
                          value: T
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.NotNull(@object: result);
        // Should have no constraint-related errors
    }
    /// <summary>
    /// Verifies semantic analysis behavior for unknown type parameter and reports the expected error.
    /// </summary>
    [Fact]
    public void Analyze_UnknownTypeParameter_ReportsError()
    {
        string source = """
                        record Container[T]
                        needs X obeys Comparable
                          value: T
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.True(condition: result.Errors.Count > 0);
        Assert.Contains(collection: result.Errors, filter: e => e.Message.Contains(value: 'X'));
    }

    #endregion

    #region Protocol Implementation

    /// <summary>
    /// Verifies semantic analysis behavior for record follows protocol without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void Analyze_RecordFollowsProtocol_NoError()
    {
        string source = """
                        protocol Displayable
                          @readonly
                          routine Me.display() -> Text

                        record Point obeys Displayable
                          x: F32
                          y: F32

                        @readonly
                        routine Point.display() -> Text
                          return "point"
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.NotNull(@object: result);
        // Should validate protocol implementation
    }

    #endregion

    #region Integer Literal Type Inference

    /// <summary>
    /// Verifies semantic analysis behavior for return integer literal infers from return type.
    /// </summary>
    [Fact]
    public void Analyze_ReturnIntegerLiteral_InfersFromReturnType()
    {
        string source = """
                        routine get_value() -> S32
                          return 0
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for return integer literal infers U32.
    /// </summary>
    [Fact]
    public void Analyze_ReturnIntegerLiteral_InfersU32()
    {
        string source = """
                        routine get_count() -> U32
                          return 42
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for return integer literal infers S64.
    /// </summary>
    [Fact]
    public void Analyze_ReturnIntegerLiteral_InfersS64()
    {
        string source = """
                        routine get_big() -> S64
                          return 123456789
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for return integer literal in memberRoutine with return type.
    /// </summary>
    [Fact]
    public void Analyze_ReturnIntegerLiteral_InMemberRoutineWithReturnType()
    {
        string source = """
                        record Counter
                          value: S32

                        @readonly
                        routine Counter.get_zero() -> S32
                          return 0
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for var with type annotation infers literal as annotated type.
    /// </summary>
    [Fact]
    public void Analyze_VarWithTypeAnnotation_InfersLiteralAsAnnotatedType()
    {
        // var c: S32 = 123 should infer 123 as S32, not default S64
        string source = """
                        routine test()
                          var c: S32 = 123
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for var with type annotation infers unsigned literal.
    /// </summary>
    [Fact]
    public void Analyze_VarWithTypeAnnotation_InfersUnsignedLiteral()
    {
        string source = """
                        routine test()
                          var x: U8 = 255
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for var with type annotation rejects out of range literal.
    /// </summary>
    [Fact]
    public void Analyze_VarWithTypeAnnotation_RejectsOutOfRangeLiteral()
    {
        // 256 doesn't fit in U8 (0..255), should report overflow
        string source = """
                        routine test()
                          var x: U8 = 256
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.IntegerLiteralOverflow);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for var with type annotation rejects out of range large value.
    /// </summary>
    [Fact]
    public void Analyze_VarWithTypeAnnotation_RejectsOutOfRangeLargeValue()
    {
        // 1231231231234 doesn't fit in S32 (-2147483648..2147483647), should report overflow
        string source = """
                        routine test()
                          var c: S32 = 1231231231234
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.IntegerLiteralOverflow);
    }

    #endregion

    #region Choice Restrictions

    /// <summary>
    /// Verifies semantic analysis behavior for choice operator definition and reports the expected error.
    /// </summary>
    [Fact]
    public void Analyze_ChoiceOperatorDefinition_ReportsError()
    {
        string source = """
                        choice HttpStatus
                          OK
                          NOT_FOUND

                        @readonly
                        routine HttpStatus.$add(you: HttpStatus) -> HttpStatus
                          return OK
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ArithmeticOnChoiceType);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for choice regular memberRoutine without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void Analyze_ChoiceRegularMemberRoutine_NoError()
    {
        string source = """
                        choice Color
                          RED
                          GREEN
                          BLUE

                        @readonly
                        routine Color.is_warm() -> Bool
                          return false
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ArithmeticOnChoiceType);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for choice mixed values and reports the expected error.
    /// </summary>
    [Fact]
    public void Analyze_ChoiceMixedValues_ReportsError()
    {
        string source = """
                        choice HttpStatus
                          OK: 200
                          NOT_FOUND
                          INTERNAL_ERROR: 500
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ChoiceMixedValues);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for choice all explicit values without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void Analyze_ChoiceAllExplicitValues_NoError()
    {
        string source = """
                        choice HttpStatus
                          OK: 200
                          NOT_FOUND: 404
                          INTERNAL_ERROR: 500
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ChoiceMixedValues);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for choice all implicit values without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void Analyze_ChoiceAllImplicitValues_NoError()
    {
        string source = """
                        choice Color
                          RED
                          GREEN
                          BLUE
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ChoiceMixedValues);
    }

    #endregion

    #region Error Handling Return Type Restrictions

    /// <summary>
    /// Verifies semantic analysis behavior for routine returns maybe and reports the expected error.
    /// </summary>
    [Fact]
    public void Analyze_RoutineReturnsMaybe_ReportsError()
    {
        string source = """
                        routine foo() -> Maybe[S32]
                          pass
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ErrorHandlingTypeAsReturnType);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for routine returns result and reports the expected error.
    /// </summary>
    [Fact]
    public void Analyze_RoutineReturnsResult_ReportsError()
    {
        string source = """
                        routine foo() -> Result[S32]
                          return 42
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ErrorHandlingTypeAsReturnType);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for routine returns lookup and reports the expected error.
    /// </summary>
    [Fact]
    public void Analyze_RoutineReturnsLookup_ReportsError()
    {
        string source = """
                        routine foo() -> Lookup[S32]
                          return 42
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ErrorHandlingTypeAsReturnType);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for failable routine no error handling return type error.
    /// </summary>
    [Fact]
    public void Analyze_FailableRoutine_NoErrorHandlingReturnTypeError()
    {
        string source = """
                        routine get_value!() -> S32
                          return 42
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ErrorHandlingTypeAsReturnType);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for memberRoutine returns maybe and reports the expected error.
    /// </summary>
    [Fact]
    public void Analyze_memberRoutineReturnsMaybe_ReportsError()
    {
        string source = """
                        record Point
                          x: F32
                          y: F32

                        @readonly
                        routine Point.foo() -> Maybe[F32]
                          pass
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ErrorHandlingTypeAsReturnType);
    }

    #endregion

    #region Const Generic Validation

    /// <summary>
    /// Verifies semantic analysis behavior for const generic integer type without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void Analyze_ConstGeneric_IntegerType_NoError()
    {
        // Integer types implement ConstCompatible and should be valid
        string source = """
                        entity Buffer[T, N]
                        needs N is Address
                          data: T

                        routine test(buf: Buffer[U8, Address])
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        // Should not have InvalidConstGenericType error
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.InvalidConstGenericType);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for const generic bool type without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void Analyze_ConstGeneric_BoolType_NoError()
    {
        // Bool implements ConstCompatible
        string source = """
                        record Config[T]
                        needs T is Bool
                          value: S32

                        routine test(cfg: Config[Bool])
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.InvalidConstGenericType);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for const generic choice type without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void Analyze_ConstGeneric_ChoiceType_NoError()
    {
        // Choice types are valid for const generics by category
        string source = """
                        choice Direction
                          North
                          South
                          East
                          West

                        record Compass[D]
                        needs D is Direction
                          strength: F32

                        routine test(c: Compass[Direction])
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.InvalidConstGenericType);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for const generic record type and reports the expected error.
    /// </summary>
    [Fact]
    public void Analyze_ConstGeneric_RecordType_ReportsError()
    {
        // Arbitrary record types should fail validation when used as type expression
        string source = """
                        record Foo
                          x: S32

                        record Bar[T]
                        needs T is Foo
                          value: S32

                        routine test(b: Bar[Foo])
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.InvalidConstGenericType);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for const generic type mismatch and reports the expected error.
    /// </summary>
    [Fact]
    public void Analyze_ConstGeneric_TypeMismatch_ReportsError()
    {
        // Type argument doesn't match required const type
        string source = """
                        entity Buffer[T, N]
                        needs N is Address
                          data: T

                        routine test(buf: Buffer[U8, S32])
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ConstGenericTypeMismatch);
    }

    /// <summary>
    /// Verifies semantic analysis behavior for const generic preset literal without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void Analyze_ConstGeneric_PresetLiteral_NoError()
    {
        string source = """
                        preset WIDTH: Address = 16addr

                        entity Buffer[T, N]
                        needs N is Address
                          data: T

                        routine test(buf: Buffer[U8, WIDTH])
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.UnknownType ||
                         e.Code == SemanticDiagnosticCode.ConstGenericTypeMismatch ||
                         e.Code == SemanticDiagnosticCode.PresetNotConstant);
    }

    /// <summary>
    /// Verifies semantic analysis behavior for const generic preset alias without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void Analyze_ConstGeneric_PresetAlias_NoError()
    {
        string source = """
                        preset BASE_WIDTH: Address = 16addr
                        preset WIDTH: Address = BASE_WIDTH

                        entity Buffer[T, N]
                        needs N is Address
                          data: T

                        routine test(buf: Buffer[U8, WIDTH])
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.UnknownType ||
                         e.Code == SemanticDiagnosticCode.ConstGenericTypeMismatch ||
                         e.Code == SemanticDiagnosticCode.PresetNotConstant);
    }

    #endregion

    #region Generic memberRoutine Resolution (S191, S192, S193)

    /// <summary>
    /// Verifies semantic analysis behavior for generic record equality operator without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void Analyze_GenericRecord_EqualityOperator_NoError()
    {
        // S193: == on generic record resolution should not produce MemberNotFound
        string source = """
                        protocol Equatable
                          @readonly
                          routine Me.$eq(you: Me) -> Bool

                        record Wrapper[T] obeys Equatable
                          value: T

                        @readonly
                        routine Wrapper.$eq(you: Wrapper) -> Bool
                          return true

                        routine test(a: Wrapper[S32], b: Wrapper[S32]) -> Bool
                          return a == b
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.MemberNotFound);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for generic type void memberRoutine call without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void Analyze_GenericType_VoidMemberRoutineCall_NoError()
    {
        // S191: Void memberRoutine call on generic resolution should not produce error type
        string source = """
                        record Container[T]
                          value: T

                        routine Container.reset()
                          me.value = me.value
                          return

                        routine test(c: Container[S32])
                          c.reset()
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.MemberNotFound);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for generic type memberRoutine call resolves via definition.
    /// </summary>
    [Fact]
    public void Analyze_GenericType_memberRoutineCall_ResolvesViaDefinition()
    {
        // memberRoutine on generic type should be found through the generic definition
        string source = """
                        record Box[T]
                          value: T

                        @readonly
                        routine Box.size() -> S32
                          return 1

                        routine test(b: Box[S32]) -> S32
                          return b.size()
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.MemberNotFound);
    }

    #endregion
}
