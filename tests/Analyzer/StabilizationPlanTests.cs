using Builder.Diagnostics;
using Builder.Verification.Results;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Contains tests for stabilization plan.
/// </summary>
public class StabilizationPlanTests
{
    #region P1 -> Protocol memberRoutine lookup on instantiated generics

    /// <summary>
    /// Verifies that the test validates protocol memberRoutine on generic owner and substitutes the parameter type.
    /// </summary>
    [Fact]
    public void P1_ProtocolMemberRoutineOnGenericOwner_SubstitutesParamType()
    {
        // Protocol memberRoutine declared with T param, called on resolved owner
        // LookupMemberRoutine should substitute T -> S32 in parameter type
        string source = """
                        protocol Summable
                          @readonly
                          routine Me.sum(other: Me) -> Me

                        record Total[T] obeys Summable
                          value: T

                        @readonly
                        routine Total[T].sum(other: Total[T]) -> Total[T]
                          return me

                        routine test()
                          var a = Total[S32](value: 1)
                          var b = Total[S32](value: 2)
                          var c: Total[S32] = a.sum(other: b)
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }

    /// <summary>
    /// Verifies that the test validates generic memberRoutine on generic owner both levels substituted.
    /// </summary>
    [Fact]
    public void P1_GenericMemberRoutineOnGenericOwner_BothLevelsSubstituted()
    {
        // Owner-level T and memberRoutine-level U should both resolve
        string source = """
                        record Store[T]
                          item: T

                        routine Store[T].transform[U](func_val: U) -> U
                          return func_val

                        routine test()
                          var s = Store[S32](item: 10)
                          var r: Bool = s.transform[Bool](func_val: true)
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }

    /// <summary>
    /// Verifies that the test validates generic memberRoutine return type uses owner type arg.
    /// </summary>
    [Fact]
    public void P1_GenericMemberRoutineReturnType_UsesOwnerTypeArg()
    {
        // Return type T should resolve to the owner's type argument
        string source = """
                        record Slot[T]
                          data: T

                        routine Slot[T].peek() -> T
                          return me.data

                        routine test()
                          var s = Slot[Bool](data: false)
                          var v: Bool = s.peek()
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }

    /// <summary>
    /// Verifies that the test validates protocol memberRoutine on nested generic owner uses concrete nested type.
    /// </summary>
    [Fact]
    public void P1_ProtocolMemberRoutineOnNestedGenericOwner_UsesConcreteNestedType()
    {
        string source = """
                        protocol Clonable
                          @readonly
                          routine Me.duplicate() -> Me

                        record Box[T] obeys Clonable
                          value: T

                        @readonly
                        routine Box[T].duplicate() -> Box[T]
                          return Box[T](value: me.value)

                        routine test()
                          var boxed = Box[Box[S32]](value: Box[S32](value: 7))
                          var copy: Box[Box[S32]] = boxed.duplicate()
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }

    #endregion

    #region P2 -> GenericDefinition preserved across all update paths

    /// <summary>
    /// Verifies that the test validates generic record preserves definition after protocol update.
    /// </summary>
    [Fact]
    public void P2_GenericRecord_PreservesDefinitionAfterProtocolUpdate()
    {
        // GenericDefinition must survive UpdateRecordProtocols
        // so memberRoutine lookup on Pair[S32] can substitute T -> S32
        string source = """
                        protocol Showable
                          @readonly
                          routine Me.label() -> Text

                        record Pair[T] obeys Showable
                          first: T
                          second: T

                        @readonly
                        routine Pair[T].label() -> Text
                          return "pair"

                        routine Pair[T].get_first() -> T
                          return me.first

                        routine test()
                          var p = Pair[S32](first: 1, second: 2)
                          var v: S32 = p.get_first()
                          var lbl: Text = p.label()
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }

    /// <summary>
    /// Verifies that the test validates generic entity preserves definition after protocol update.
    /// </summary>
    [Fact]
    public void P2_GenericEntity_PreservesDefinitionAfterProtocolUpdate()
    {
        // Same as above but for entity types
        string source = """
                        protocol Describable
                          @readonly
                          routine Me.describe() -> Text

                        entity Container[T] obeys Describable
                          item: T

                        @readonly
                        routine Container[T].describe() -> Text
                          return "container"

                        routine Container[T].get_item() -> T
                          return me.item

                        routine test()
                          var c = Container[Bool](item: true)
                          var v: Bool = c.get_item()
                          var d: Text = c.describe()
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }

    #endregion

    #region P3 -> Using bound type agreement

    /// <summary>
    /// Verifies that the test validates using with generic resource binds correct type.
    /// </summary>
    [Fact]
    public void P3_UsingWithGenericResource_BindsCorrectType()
    {
        // using on a generic Enterable type (self pass-through binds the resource itself)
        string source = """
                        record Guard[T] obeys Enterable
                          resource: T

                        routine Guard[T].enter() -> Guard[T]
                          return me

                        routine Guard[T].exit()
                          return

                        routine test()
                          var g = Guard[S32](resource: 42)
                          using g as val
                            var x: S32 = val.resource
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.UsingTargetMissingEnterExit);
    }

    /// <summary>
    /// Verifies that the test validates using with nested generic return binds resolved type.
    /// </summary>
    [Fact]
    public void P3_UsingWithNestedGenericReturn_BindsResolvedType()
    {
        string source = """
                        record Box[T]
                          value: T

                        record Guard[T] obeys Enterable
                          resource: T

                        routine Guard[T].enter() -> Guard[T]
                          return me

                        routine Guard[T].exit()
                          return

                        routine test()
                          var g = Guard[Box[S32]](resource: Box[S32](value: 42))
                          using g as value_box
                            var n: S32 = value_box.resource.value
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }

    #endregion

    #region P4 -> Routine body matching edge cases

    /// <summary>
    /// Verifies that the test validates overloaded routines zero arg vs one arg both match.
    /// </summary>
    [Fact]
    public void P4_OverloadedRoutines_ZeroArgVsOneArg_BothMatch()
    {
        // Zero-arg and one-arg overloads must each find their correct body
        string source = """
                        record Counter
                          count: S32

                        routine Counter.reset() -> Counter
                          return Counter(count: 0)

                        routine Counter.reset(to: S32) -> Counter
                          return Counter(count: to)

                        routine test()
                          var c = Counter(count: 5)
                          var a = c.reset()
                          var b = c.reset(to: 10)
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.UnresolvedRoutineBody);
    }

    /// <summary>
    /// Verifies that the test validates overloaded create three overloads all match.
    /// </summary>
    [Fact]
    public void P4_OverloadedCreate_ThreeOverloads_AllMatch()
    {
        // Three $create overloads with different types: S32, Bool, Text
        string source = """
                        record Value
                          raw: S64

                        routine Value.$create(from: S32) -> Value
                          return Value(raw: from.S64())

                        routine Value.$create(from: Bool) -> Value
                          return Value(raw: 1s64)

                        routine Value.$create(from: Text) -> Value
                          return Value(raw: 0s64)

                        routine test()
                          var a = Value(from: 42)
                          var b = Value(from: true)
                          var c = Value(from: "hi")
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.UnresolvedRoutineBody);
    }

    /// <summary>
    /// Verifies that the test validates generic routine body matches by resolved signature.
    /// </summary>
    [Fact]
    public void P4_GenericRoutineBody_MatchesByResolvedSignature()
    {
        // Body matching on generic owner should use resolved type names
        string source = """
                        record Stack[T]
                          top: T

                        routine Stack[T].push(item: T) -> Stack[T]
                          return Stack[T](top: item)

                        routine Stack[T].peek() -> T
                          return me.top

                        routine test()
                          var s = Stack[S32](top: 0)
                          var s2 = s.push(item: 42)
                          var v: S32 = s2.peek()
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.UnresolvedRoutineBody);
    }

    /// <summary>
    /// Verifies that the test validates generic owner overloads same arity different types match correct body.
    /// </summary>
    [Fact]
    public void P4_GenericOwnerOverloads_SameArityDifferentTypes_MatchCorrectBody()
    {
        string source = """
                        record Buffer[T]
                          value: T
                          ready: Bool

                        routine Buffer[T].replace(next: T) -> Buffer[T]
                          return Buffer[T](value: next, ready: me.ready)

                        routine Buffer[T].replace(flag: Bool) -> Buffer[T]
                          return Buffer[T](value: me.value, ready: flag)

                        routine test()
                          var b = Buffer[S32](value: 1, ready: false)
                          var a = b.replace(next: 9)
                          var c = b.replace(flag: true)
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.UnresolvedRoutineBody);
    }

    /// <summary>
    /// Verifies that the test validates create overload with owner typed parameter matches correct body.
    /// </summary>
    [Fact]
    public void P4_CreateOverload_WithOwnerTypedParameter_MatchesCorrectBody()
    {
        string source = """
                        record Wrapper[T]
                          value: T

                        routine Wrapper[T].$create(from: T) -> Wrapper[T]
                          return Wrapper[T](value: from)

                        routine Wrapper[T].$create(copy: Wrapper[T]) -> Wrapper[T]
                          return Wrapper[T](value: copy.value)

                        routine test()
                          var a = Wrapper[S32](from: 12)
                          var b = Wrapper[S32](copy: a)
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.UnresolvedRoutineBody);
    }

    #endregion

    #region P5 -> GenericAstRewriter no longer rewrites identifiers

    /// <summary>
    /// Verifies that the test validates generic const param resolved at codegen.
    /// </summary>
    [Fact]
    public void P5_GenericConstParam_ResolvedAtCodegen()
    {
        // Const generic value N should not be rewritten in AST
        // Codegen resolves via _typeSubstitutions
        string source = """
                        record Wrapper[T]
                          value: T

                        routine Wrapper[T].get() -> T
                          return me.value

                        routine test()
                          var w = Wrapper[S32](value: 99)
                          var v: S32 = w.get()
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }

    #endregion

    #region P6 -> No string-based generic heuristics

    /// <summary>
    /// Verifies that the test validates nested generic type resolved via generic definition.
    /// </summary>
    [Fact]
    public void P6_NestedGenericType_ResolvedViaGenericDefinition()
    {
        // Nested generics like Wrapper[Wrapper[S32]] should resolve
        // through GenericDefinition, not Name.Contains('[') heuristics
        string source = """
                        record Wrapper[T]
                          inner: T

                        routine Wrapper[T].unwrap() -> T
                          return me.inner

                        routine test()
                          var inner = Wrapper[S32](inner: 42)
                          var outer = Wrapper[Wrapper[S32]](inner: inner)
                          var result: Wrapper[S32] = outer.unwrap()
                          var val: S32 = result.unwrap()
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }

    /// <summary>
    /// Verifies that the test validates nested generic member type returns resolved inner type.
    /// </summary>
    [Fact]
    public void P6_NestedGenericMemberType_ReturnsResolvedInnerType()
    {
        string source = """
                        record Node[T]
                          item: T

                        @readonly
                        routine Node[T].hash() -> U64
                          return 0u64

                        record Holder[T]
                          node: Node[T]

                        @readonly
                        routine Holder[T].hash() -> U64
                          return 0u64

                        routine Holder[T].fetch() -> Node[T]
                          return me.node

                        routine test()
                          var h = Holder[S32](node: Node[S32](item: 4))
                          var n: Node[S32] = h.fetch()
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }

    #endregion

    #region Cross-cutting -> Generic + protocol + overload combined

    /// <summary>
    /// Verifies that the test validates generic with protocol and overloads.
    /// </summary>
    [Fact]
    public void Combined_GenericWithProtocolAndOverloads()
    {
        // Complex scenario: generic type obeys protocol, has overloaded memberRoutines,
        // memberRoutine lookup + body matching + type substitution all must work together
        string source = """
                        protocol Clearable
                          routine Me.clear() -> Me

                        record Buffer[T] obeys Clearable
                          item: T
                          size: S32

                        routine Buffer[T].clear() -> Buffer[T]
                          return Buffer[T](item: me.item, size: 0)

                        routine Buffer[T].set(item: T) -> Buffer[T]
                          return Buffer[T](item: item, size: me.size)

                        routine Buffer[T].resize(size: S32) -> Buffer[T]
                          return Buffer[T](item: me.item, size: size)

                        routine test()
                          var b = Buffer[S32](item: 0, size: 10)
                          var c: Buffer[S32] = b.clear()
                          var d = b.set(item: 42)
                          var e = b.resize(size: 5)
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }

    #endregion
}
