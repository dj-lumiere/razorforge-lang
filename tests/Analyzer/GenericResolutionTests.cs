using Compiler.Diagnostics;
using Compiler.Verification;
using Compiler.Verification.Results;
using SyntaxTree;
using TypeModel.Enums;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Contains tests for generic resolution.
/// </summary>
public class GenericResolutionTests
{
    #region S191 — Void return on generic memberRoutine calls

    /// <summary>
    /// Verifies semantic analysis behavior for generic void memberRoutine returns blank.
    /// </summary>
    [Fact]
    public void Analyze_GenericVoidMemberRoutine_ReturnsNone()
    {
        // S191: Calling a void memberRoutine on a generic resolution should return None, not <error>
        string source = """
                        record Box[T]
                          value: T

                        routine Box[T].clear()
                          return

                        routine test()
                          var b = Box[S32](value: 42)
                          b.clear()
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }

    #endregion

    #region S192 — Double-generic memberRoutine return type

    /// <summary>
    /// Verifies semantic analysis behavior for memberRoutine level generic return type resolves correctly.
    /// </summary>
    [Fact]
    public void Analyze_memberRoutineLevelGenericReturnType_ResolvesCorrectly()
    {
        // S192: memberRoutine-level generic params + owner-level generic params
        // convert[U](new_val: U) -> Box[U] should resolve Box[U] with the call-site type arg
        string source = """
                        record Box[T]
                          value: T

                        routine Box[T].convert[U](new_val: U) -> Box[U]
                          return Box[U](value: new_val)

                        routine test()
                          var b = Box[S32](value: 42)
                          var c: Box[Bool] = b.convert[Bool](true)
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for memberRoutine level generic return type infers without annotation.
    /// </summary>
    [Fact]
    public void Analyze_memberRoutineLevelGenericReturnType_InfersWithoutAnnotation()
    {
        // S192: Without explicit type annotation, var c should infer as Box[Bool]
        string source = """
                        record Box[T]
                          value: T

                        routine Box[T].convert[U](new_val: U) -> Box[U]
                          return Box[U](value: new_val)

                        routine test()
                          var b = Box[S32](value: 42)
                          var c = b.convert[Bool](true)
                          var d: Box[Bool] = c
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for memberRoutine level generic direct return resolves correctly.
    /// </summary>
    [Fact]
    public void Analyze_memberRoutineLevelGenericDirectReturn_ResolvesCorrectly()
    {
        // S192: memberRoutine returning U directly (not wrapped in owner type)
        string source = """
                        record Box[T]
                          value: T

                        routine Box[T].extract[U](val: U) -> U
                          return val

                        routine test()
                          var b = Box[S32](value: 42)
                          var x: Bool = b.extract[Bool](true)
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }

    #endregion

    #region S193 — $eq on generic record types

    /// <summary>
    /// Verifies semantic analysis behavior for generic record memberRoutine lookup works on resolution.
    /// </summary>
    [Fact]
    public void Analyze_GenericRecordMemberRoutineLookup_WorksOnResolution()
    {
        // S193: memberRoutines registered on generic def (Wrapper) should be found for Wrapper[S32]
        // LookupMemberRoutine falls back from Wrapper[S32] → Wrapper to find user-defined memberRoutines
        string source = """
                        record Wrapper[T]
                          value: T

                        routine Wrapper[T].unwrap() -> T
                          return me.value

                        routine test()
                          var a = Wrapper[S32](value: 1)
                          var v: S32 = a.unwrap()
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }

    #endregion

    #region LookupMemberRoutine fully-resolved results

    /// <summary>
    /// Verifies semantic analysis behavior for generic owner memberRoutine param type substituted.
    /// </summary>
    [Fact]
    public void Analyze_GenericOwnerMemberRoutine_ParamTypeSubstituted()
    {
        // LookupMemberRoutine on List[S32].$add should return param type S32 (not T)
        // After fix, no manual SubstituteTypeParameters needed at call site
        string source = """
                        record Pair[T]
                          first: T
                          second: T

                        routine Pair[T].swap_first(value: T) -> T
                          return me.first

                        routine test()
                          var p = Pair[S32](first: 1, second: 2)
                          var old: S32 = p.swap_first(value: 3)
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for generic owner memberRoutine return type substituted.
    /// </summary>
    [Fact]
    public void Analyze_GenericOwnerMemberRoutine_ReturnTypeSubstituted()
    {
        // LookupMemberRoutine on generic owner should substitute T in return type
        string source = """
                        record Container[T]
                          item: T

                        routine Container[T].get() -> T
                          return me.item

                        routine test()
                          var c = Container[Bool](item: true)
                          var v: Bool = c.get()
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for generic owner memberRoutine nested generic substitution.
    /// </summary>
    [Fact]
    public void Analyze_GenericOwnerMemberRoutine_NestedGenericSubstitution()
    {
        // Dict[Text, List[S32]].$getitem should return List[S32], not T
        string source = """
                        record Mapping[K, V]
                          key: K
                          val: V

                        routine Mapping[K, V].get_val() -> V
                          return me.val

                        routine test()
                          var inner = Mapping[S32, Bool](key: 1, val: true)
                          var m = Mapping[Bool, Mapping[S32, Bool]](key: false, val: inner)
                          var result: Mapping[S32, Bool] = m.get_val()
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }

    #endregion

    #region P1 — ResolvedRoutine stored for member calls

    /// <summary>
    /// Verifies semantic analysis behavior for member call on generic resolution and stores the resolved routine metadata.
    /// </summary>
    [Fact]
    public void Analyze_MemberCallOnGenericResolution_StoresResolvedRoutine()
    {
        // P1: Member call on a generic resolution should store ResolvedRoutine with substituted types
        string source = """
                        record Pair[T]
                          first: T
                          second: T

                        routine Pair[T].swap_first(value: T) -> T
                          return me.first

                        routine test()
                          var p = Pair[S32](first: 1, second: 2)
                          var old = p.swap_first(value: 3)
                          return
                        """;

        Program program = Parse(source: source);
        var analyzer = new SemanticVerifier(language: Language.RazorForge) { SaOnly = true };
        AnalysisResult result = analyzer.Analyze(program: program);
        Assert.Empty(collection: result.Errors);

        // Find the 'test' routine, get its body, find the call to swap_first
        RoutineDeclaration testRoutine = program.Declarations
                                                .OfType<RoutineDeclaration>()
                                                .First(predicate: r => r.Name == "test");
        var body = (BlockStatement)testRoutine.Body;
        // Statement 1 (index 1): var old = p.swap_first(value: 3)
        var declStmt = (DeclarationStatement)body.Statements[index: 1];
        var varDecl = (VariableDeclaration)declStmt.Declaration;
        var call = (CallExpression)varDecl.Initializer!;

        Assert.NotNull(@object: call.ResolvedRoutine);
        Assert.Equal(expected: "swap_first", actual: call.ResolvedRoutine!.Name);
        // Return type should be substituted: T → S32
        Assert.NotNull(@object: call.ResolvedRoutine.ReturnType);
        Assert.Equal(expected: "S32", actual: call.ResolvedRoutine.ReturnType!.Name);
    }

    /// <summary>
    /// Verifies semantic analysis behavior for void member call on generic resolution and stores the resolved routine metadata.
    /// </summary>
    [Fact]
    public void Analyze_VoidMemberCallOnGenericResolution_StoresResolvedRoutine()
    {
        // P1: Void memberRoutine on generic resolution should still store ResolvedRoutine
        string source = """
                        record Box[T]
                          value: T

                        routine Box[T].clear()
                          return

                        routine test()
                          var b = Box[S32](value: 42)
                          b.clear()
                          return
                        """;

        Program program = Parse(source: source);
        var analyzer = new SemanticVerifier(language: Language.RazorForge) { SaOnly = true };
        AnalysisResult result = analyzer.Analyze(program: program);
        Assert.Empty(collection: result.Errors);

        RoutineDeclaration testRoutine = program.Declarations
                                                .OfType<RoutineDeclaration>()
                                                .First(predicate: r => r.Name == "test");
        var body = (BlockStatement)testRoutine.Body;
        // Statement 1 (index 1): b.clear()
        var exprStmt = (ExpressionStatement)body.Statements[index: 1];
        var call = (CallExpression)exprStmt.Expression;

        Assert.NotNull(@object: call.ResolvedRoutine);
        Assert.Equal(expected: "clear", actual: call.ResolvedRoutine!.Name);
    }

    /// <summary>
    /// Verifies semantic analysis behavior for generic memberRoutine call and stores the resolved routine metadata.
    /// </summary>
    [Fact]
    public void Analyze_GenericMemberRoutineCall_StoresResolvedRoutine()
    {
        // P1: Generic memberRoutine call (obj.MemberRoutine[U](args)) stores ResolvedRoutine
        string source = """
                        record Box[T]
                          value: T

                        routine Box[T].convert[U](new_val: U) -> Box[U]
                          return Box[U](value: new_val)

                        routine test()
                          var b = Box[S32](value: 42)
                          var c = b.convert[Bool](true)
                          return
                        """;

        Program program = Parse(source: source);
        // Needs full pipeline: GenericCallLoweringPass (Phase 4) lowers GenericMemberRoutineCallExpression → CallExpression
        var analyzer = new SemanticVerifier(language: Language.RazorForge);
        AnalysisResult result = analyzer.Analyze(program: program);
        Assert.Empty(collection: result.Errors);

        RoutineDeclaration testRoutine = program.Declarations
                                                .OfType<RoutineDeclaration>()
                                                .First(predicate: r => r.Name == "test");
        var body = (BlockStatement)testRoutine.Body;
        // Statement 1 (index 1): var c = b.convert[Bool](true)
        // After GenericCallLoweringPass, the node is lowered to a CallExpression with TypeArguments.
        var declStmt = (DeclarationStatement)body.Statements[index: 1];
        var varDecl = (VariableDeclaration)declStmt.Declaration;
        var call = (CallExpression)varDecl.Initializer!;

        Assert.NotNull(@object: call.ResolvedRoutine);
        Assert.Equal(expected: "convert", actual: call.ResolvedRoutine!.Name);
        Assert.NotNull(@object: call.TypeArguments);
        Assert.Single(collection: call.TypeArguments!);
    }

    #endregion

    #region P2 — GenericDefinition preserved after type updates

    /// <summary>
    /// Verifies semantic analysis behavior for generic record preserves definition after member update.
    /// </summary>
    [Fact]
    public void Analyze_GenericRecord_PreservesDefinitionAfterMemberUpdate()
    {
        // P2: After UpdateRecordMemberVariables, GenericDefinition must survive
        // so that memberRoutine lookup on the resolved type can substitute T → S32
        string source = """
                        record Cell[T]
                          data: T

                        routine Cell[T].extract() -> T
                          return me.data

                        routine test()
                          var c = Cell[S32](data: 42)
                          var v: S32 = c.extract()
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for generic entity preserves definition after member update.
    /// </summary>
    [Fact]
    public void Analyze_GenericEntity_PreservesDefinitionAfterMemberUpdate()
    {
        // P2: Same test for entity types — GenericDefinition must survive update
        string source = """
                        entity Node[T]
                          value: T

                        routine Node[T].get_value() -> T
                          return me.value

                        routine test()
                          var n = Node[Bool](value: true)
                          var v: Bool = n.get_value()
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }

    #endregion

    #region P1 — Protocol memberRoutine lookup on instantiated generic protocols

    /// <summary>
    /// Verifies semantic analysis behavior for generic protocol memberRoutine and substitutes the return type.
    /// </summary>
    [Fact]
    public void Analyze_GenericProtocolMemberRoutine_SubstitutesReturnType()
    {
        // P1: Calling a protocol memberRoutine on a type that obeys a generic protocol
        // should substitute T in the return type
        string source = """
                        protocol Supplier[T]
                          @readonly
                          routine Me.supply() -> T

                        record IntSupplier obeys Supplier[S32]
                          value: S32

                        @readonly
                        routine IntSupplier.supply() -> S32
                          return me.value

                        routine test()
                          var s = IntSupplier(value: 42)
                          var v: S32 = s.supply()
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }

    /// <summary>
    /// Verifies semantic analysis behavior for generic protocol memberRoutine and substitutes the parameter type.
    /// </summary>
    [Fact]
    public void Analyze_GenericProtocolMemberRoutine_SubstitutesParamType()
    {
        // P1: Protocol memberRoutine with generic parameter type should substitute correctly
        string source = """
                        protocol Acceptor[T]
                          routine Me.accept(item: T) -> None

                        record S32Acceptor obeys Acceptor[S32]
                          count: S32

                        routine S32Acceptor.accept(item: S32) -> None
                          return

                        routine test()
                          var a = S32Acceptor(count: 0)
                          a.accept(item: 42)
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }

    /// <summary>
    /// Verifies semantic analysis behavior for generic protocol multi param and substitutes generic parameters correctly.
    /// </summary>
    [Fact]
    public void Analyze_GenericProtocol_MultiParam_SubstitutesCorrectly()
    {
        // P1: Generic protocol with multiple type parameters — both K and V
        // should be substituted in memberRoutine signatures
        string source = """
                        protocol Mapper[K, V]
                          @readonly
                          routine Me.map_value(key: K) -> V

                        record IntToBoMapper obeys Mapper[S32, Bool]
                          flag: Bool

                        @readonly
                        routine IntToBoMapper.map_value(key: S32) -> Bool
                          return me.flag

                        routine test()
                          var m = IntToBoMapper(flag: true)
                          var v: Bool = m.map_value(key: 1)
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }

    #endregion

    #region P4 — Routine body matching by resolved signature

    /// <summary>
    /// Verifies semantic analysis behavior for overloaded routines different generic params and matches the correct overloaded bodies.
    /// </summary>
    [Fact]
    public void Analyze_OverloadedRoutines_DifferentGenericParams_MatchCorrectBodies()
    {
        // P4: Two overloads with different generic parameter types
        // must each match their own body via resolved RegistryKey
        string source = """
                        record Box[T]
                          value: T

                        routine process(item: Box[S32]) -> S32
                          return item.value

                        routine process(item: Box[Bool]) -> Bool
                          return item.value

                        routine test()
                          var a = Box[S32](value: 42)
                          var b = Box[Bool](value: true)
                          var x: S32 = process(item: a)
                          var y: Bool = process(item: b)
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }

    /// <summary>
    /// Verifies semantic analysis behavior for extension routine generic owner and matches the correct body.
    /// </summary>
    [Fact]
    public void Analyze_ExtensionRoutine_GenericOwner_MatchesBody()
    {
        // P4: Extension-syntax routine with generic owner resolves its body
        string source = """
                        record Wrapper[T]
                          inner: T

                        routine Wrapper[T].get_inner() -> T
                          return me.inner

                        routine test()
                          var w = Wrapper[S32](inner: 10)
                          var v: S32 = w.get_inner()
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }

    /// <summary>
    /// Verifies semantic analysis behavior for overloaded routines plain vs generic param and matches the correct overloaded bodies.
    /// </summary>
    [Fact]
    public void Analyze_OverloadedRoutines_PlainVsGenericParam_MatchCorrectBodies()
    {
        // P4: Overloads where one takes a plain type and another takes a generic resolution
        string source = """
                        record Pair[T]
                          first: T
                          second: T

                        routine describe(item: S32) -> S32
                          return item

                        routine describe(item: Pair[S32]) -> S32
                          return item.first

                        routine test()
                          var p = Pair[S32](first: 1, second: 2)
                          var a: S32 = describe(item: 5)
                          var b: S32 = describe(item: p)
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }

    #endregion

    #region S161 — Uninferable type argument reports cleanly (not a codegen crash)

    /// <summary>
    /// A generic routine whose type parameter appears ONLY in the return type, called with
    /// no expected-type context, cannot bind that parameter. This must surface as RF-S161
    /// during SA rather than silently passing and crashing in codegen.
    /// </summary>
    [Fact]
    public void Analyze_ReturnOnlyTypeParam_NoContext_ReportsCannotInfer()
    {
        string source = """
                        routine pick[T](flag: Bool) -> T
                          return pick[T](flag: flag)

                        routine test()
                          var r = pick(flag: true)
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.CannotInferTypeArgument);
    }

    /// <summary>
    /// The same call binds cleanly when the return-only type parameter has expected-type
    /// context (a typed variable annotation), so no RF-S161 fires.
    /// </summary>
    [Fact]
    public void Analyze_ReturnOnlyTypeParam_WithContext_NoCannotInfer()
    {
        string source = """
                        routine pick[T](flag: Bool) -> T
                          return pick[T](flag: flag)

                        routine test()
                          var r: S64 = pick(flag: true)
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.CannotInferTypeArgument);
    }

    #endregion
}
