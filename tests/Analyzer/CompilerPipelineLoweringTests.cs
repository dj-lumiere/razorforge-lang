using Compiler.CodeGen;
using Compiler.Diagnostics;
using Compiler.Instantiation;
using Compiler.Verification;
using Compiler.Verification.Results;
using System.Collections;
using System.Reflection;
using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Reprs;
using TypeModel.Symbols;
using TypeModel.Types;
using ParameterInfo = TypeModel.Symbols.ParameterInfo;
using TypeInfo = TypeModel.Types.TypeInfo;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Contains tests for compiler pipeline lowering.
/// </summary>
public class CompilerPipelineLoweringTests
{
    /// <summary>
    /// Verifies semantic analysis behavior for tuple literal is lowered to creator expression.
    /// </summary>
    [Fact]
    public void Analyze_TupleLiteral_IsLoweredToCreatorExpression()
    {
        string source = """
                        routine test()
                          var pair = (1_s32, 2_s32)
                          return
                        """;

        Program program = Parse(source: source);
        var analyzer = new SemanticVerifier(language: Language.RazorForge);
        AnalysisResult result = analyzer.Analyze(program: program);

        Assert.Empty(collection: result.Errors);
        Assert.False(condition: ContainsTupleLiteral(program: program));
    }

    /// <summary>
    /// Verifies semantic analysis behavior for direct routine call attaches lowering kind.
    /// </summary>
    [Fact]
    public void Analyze_DirectRoutineCall_AttachesLoweringKind()
    {
        string source = """
                        routine helper(value: S32) -> S32
                          return value

                        routine test() -> S32
                          return helper(1_s32)
                        """;

        Program program = Parse(source: source);
        var analyzer = new SemanticVerifier(language: Language.RazorForge);
        AnalysisResult result = analyzer.Analyze(program: program);

        Assert.Empty(collection: result.Errors);
        CallExpression call = GetReturnedCall(program: program, routineName: "test");
        Assert.Equal(expected: CallLoweringKind.DirectRoutine, actual: call.LoweringKind);
        Assert.NotNull(@object: call.ResolvedRoutine);
    }

    /// <summary>
    /// Verifies semantic analysis behavior for direct routine call attaches scalar backend repr.
    /// </summary>
    [Fact]
    public void Analyze_DirectRoutineCall_AttachesScalarBackendRepr()
    {
        string source = """
                        routine helper(value: S32) -> S32
                          return value

                        routine test() -> S32
                          return helper(1_s32)
                        """;

        Program program = Parse(source: source);
        var analyzer = new SemanticVerifier(language: Language.RazorForge);
        AnalysisResult result = analyzer.Analyze(program: program);

        Assert.Empty(collection: result.Errors);
        CallExpression call = GetReturnedCall(program: program, routineName: "test");
        Assert.NotNull(@object: call.ResolvedRepr);
        Assert.Equal(expected: BackendReprKind.Scalar, actual: call.ResolvedRepr!.Kind);
        Assert.Equal(expected: "i32", actual: call.ResolvedRepr.LlvmAbiType);
    }

    /// <summary>
    /// Verifies semantic analysis behavior for type constructor call attaches lowering kind and constructed type.
    /// </summary>
    [Fact]
    public void Analyze_TypeConstructorCall_AttachesLoweringKindAndConstructedType()
    {
        string source = """
                        import Collections.BitList

                        routine test()
                          var bits = BitList()
                          return
                        """;

        Program program = Parse(source: source);
        var analyzer = new SemanticVerifier(language: Language.RazorForge);
        AnalysisResult result = analyzer.Analyze(program: program);

        Assert.Empty(collection: result.Errors);
        CallExpression call = GetVariableInitializerCall(program: program, variableName: "bits");
        Assert.Equal(expected: CallLoweringKind.TypeConstructor, actual: call.LoweringKind);
        Assert.Equal(expected: "BitList", actual: call.ConstructedType?.Name);
    }

    /// <summary>
    /// Verifies semantic analysis behavior for generic type constructor preserves lowering kind through lowering.
    /// </summary>
    [Fact]
    public void Analyze_GenericTypeConstructor_PreservesLoweringKindThroughLowering()
    {
        string source = """
                        routine test()
                          var maybe = Maybe[S32](present: true, value: 1_s32)
                          return
                        """;

        Program program = Parse(source: source);
        var analyzer = new SemanticVerifier(language: Language.RazorForge);
        AnalysisResult result = analyzer.Analyze(program: program);

        Assert.Empty(collection: result.Errors);
        CreatorExpression creator = GetVariableInitializerCreator(program: program,
            variableName: "maybe");
        Assert.Equal(expected: CallLoweringKind.TypeConstructor, actual: creator.LoweringKind);
        Assert.Equal(expected: "Maybe[Core.S32]", actual: creator.ConstructedType?.Name);
    }

    /// <summary>
    /// Verifies semantic analysis behavior for generic free routine lowers using resolved routine.
    /// </summary>
    [Fact]
    public void Analyze_GenericFreeRoutine_LowersUsingResolvedRoutine()
    {
        string source = """
                        routine test()
                          var ptr = hijacked_none[S32]()
                          return
                        """;

        Program program = Parse(source: source);
        var analyzer = new SemanticVerifier(language: Language.RazorForge);
        AnalysisResult result = analyzer.Analyze(program: program);

        Assert.Empty(collection: result.Errors);
        CallExpression call = GetVariableInitializerCall(program: program, variableName: "ptr");
        Assert.Equal(expected: CallLoweringKind.DirectRoutine, actual: call.LoweringKind);
        Assert.NotNull(@object: call.ResolvedRoutine);
        Assert.Equal(expected: "hijacked_none", actual: call.ResolvedRoutine!.Name);
        Assert.False(condition: call.ResolvedRoutine.IsGenericDefinition);
        Assert.NotNull(@object: call.ResolvedRepr);
        Assert.Equal(expected: BackendReprKind.WrapperRef, actual: call.ResolvedRepr!.Kind);
        Assert.Equal(expected: PointerFlavor.Hijacked, actual: call.ResolvedRepr.PointerFlavor);
        Assert.Equal(expected: "ptr", actual: call.ResolvedRepr.LlvmAbiType);
    }

    /// <summary>
    /// Verifies semantic analysis behavior for monomorphized generic body is concrete before backend entry.
    /// </summary>
    [Fact]
    public void Analyze_MonomorphizedGenericBody_IsConcreteBeforeBackendEntry()
    {
        string source = """
                        record Box[T]
                          value: T

                        routine Box[T].fetch() -> T
                          return me.value

                        routine start() -> S32
                          var box = Box[S32](value: 7_s32)
                          return box.fetch()
                        """;

        Program program = Parse(source: source);
        var analyzer = new SemanticVerifier(language: Language.RazorForge);
        AnalysisResult result = analyzer.Analyze(program: program);

        Assert.Empty(collection: result.Errors);

        // `fetch` (not `peek`) — `peek` now also names the stdlib `Hijacked[T].peek` accessor, whose
        // many monomorphizations would make this filter ambiguous.
        MonomorphizedBody body = Assert.Single(
            collection: result.InstantiatedGenericBodies.Values.Where(predicate: candidate =>
                candidate.Info.Name == "fetch"));

        Assert.False(condition: ContainsGenericPlaceholder(type: body.Info.OwnerType));
        Assert.DoesNotContain(collection: body.Info.Parameters,
            filter: param => ContainsGenericPlaceholder(type: param.Type));
        Assert.False(condition: ContainsGenericPlaceholder(type: body.Info.ReturnType));
    }

    /// <summary>
    /// Verifies semantic analysis behavior for monomorphized constructor call binds concrete resolved routine.
    /// </summary>
    [Fact]
    public void Analyze_MonomorphizedConstructorCall_BindsConcreteResolvedRoutine()
    {
        string source = """
                        record Box[T]
                          value: T

                        routine Box[T].none_ptr() -> Box[T]
                          return Box[T](value: me.value)

                        routine start() -> Box[S32]
                          var box = Box[S32](value: 7_s32)
                          return box.none_ptr()
                        """;

        Program program = Parse(source: source);
        var analyzer = new SemanticVerifier(language: Language.RazorForge);
        AnalysisResult result = analyzer.Analyze(program: program);

        Assert.Empty(collection: result.Errors);

        MonomorphizedBody body = Assert.Single(
            collection: result.InstantiatedGenericBodies.Values.Where(predicate: candidate =>
                candidate.Info.Name == "none_ptr"));

        // A record memberwise construction lowers to a CreatorExpression; after monomorphization
        // its ConstructedType must be the concrete owner (Box[S32]), not the generic definition.
        BlockStatement block = Assert.IsType<BlockStatement>(@object: body.Ast.Body);
        ReturnStatement ret = Assert.IsType<ReturnStatement>(@object: block.Statements.Last());
        CreatorExpression creator = Assert.IsType<CreatorExpression>(@object: ret.Value);
        Assert.False(condition: ContainsGenericPlaceholder(type: creator.ConstructedType));
        Assert.Equal(expected: "Box[Core.S32]", actual: creator.ConstructedType?.Name);
    }

    /// <summary>
    /// Verifies semantic analysis behavior for monomorphized memberRoutine call has concrete return metadata.
    /// </summary>
    [Fact]
    public void Analyze_MonomorphizedMemberRoutineCall_HasConcreteReturnMetadata()
    {
        string source = """
                        record Box[T]
                          value: T

                        routine Box[T].peek() -> T
                          return me.value

                        routine Box[T].copy_value() -> T
                          return me.peek()

                        routine start() -> S32
                          var box = Box[S32](value: 7_s32)
                          return box.copy_value()
                        """;

        Program program = Parse(source: source);
        var analyzer = new SemanticVerifier(language: Language.RazorForge);
        AnalysisResult result = analyzer.Analyze(program: program);

        Assert.Empty(collection: result.Errors);

        MonomorphizedBody body = Assert.Single(
            collection: result.InstantiatedGenericBodies.Values.Where(predicate: candidate =>
                candidate.Info.Name == "copy_value"));

        CallExpression call = GetReturnedCall(body: body.Ast.Body);
        Assert.False(condition: ContainsGenericPlaceholder(type: call.ResolvedType));
    }

    /// <summary>
    /// Verifies semantic analysis behavior for when expression becomes is lowered before codegen.
    /// </summary>
    [Fact]
    public void Analyze_WhenExpressionBecomes_IsLoweredBeforeCodegen()
    {
        string source = """
                        routine test(value: S32) -> S32
                          var result = when value
                            == 1 =>
                              var doubled = value * 2_s32
                              becomes doubled
                            else => 0_s32
                          return result
                        """;

        Program program = Parse(source: source);
        var analyzer = new SemanticVerifier(language: Language.RazorForge);
        AnalysisResult result = analyzer.Analyze(program: program);

        Assert.Empty(collection: result.Errors);
        Assert.False(condition: ContainsBecomes(program: program));
    }

    /// <summary>
    /// Verifies semantic analysis behavior for lambda expression is lifted before codegen.
    /// </summary>
    [Fact]
    public void Analyze_LambdaExpression_IsLiftedBeforeCodegen()
    {
        // A lambda parameter needs an annotation or a typed target: RazorForge does not
        // back-infer a lambda parameter's type from its body (RF-S638). Annotate `x`.
        string source = """
                        routine test() -> S32
                          var double_it = (x: S32) => x * 2_s32
                          return 0_s32
                        """;

        Program program = Parse(source: source);
        var analyzer = new SemanticVerifier(language: Language.RazorForge);
        AnalysisResult result = analyzer.Analyze(program: program);

        Assert.Empty(collection: result.Errors);
        Assert.False(condition: ContainsLambda(program: program));
    }

    /// <summary>
    /// Verifies code generation behavior for tuple literal lowering and emits the expected IR.
    /// </summary>
    [Fact]
    public void Codegen_TupleLiteralLowering_GeneratesIr()
    {
        string source = """
                        routine test()
                          var pair = (1_s32, 2_s32)
                          return
                        """;

        Program program = Parse(source: source);
        var analyzer = new SemanticVerifier(language: Language.RazorForge);
        AnalysisResult result = analyzer.Analyze(program: program);

        Assert.Empty(collection: result.Errors);

        var generator = new LlvmCodeGenerator(program: program,
            registry: result.Registry,
            options: new LlvmCodeGeneratorOptions
            {
                StdlibPrograms = result.Registry.StdlibPrograms,
                SynthesizedBodies = result.SynthesizedBodies,
                InstantiatedGenericBodies = result.InstantiatedGenericBodies
            });

        string llvmIr = generator.Generate();
        Assert.Contains(expectedSubstring: "insertvalue", actualString: llvmIr);
    }

    /// <summary>
    /// Verifies code generation behavior for priority queue dict literal and emits the expected IR.
    /// </summary>
    [Fact]
    public void Codegen_PriorityQueueDictLiteral_GeneratesIr()
    {
        string source = """
                        import Collections
                        routine test()
                          var items: PriorityQueue[S64, Text] = {1: "high", 10: "low"}
                          return
                        """;

        Program program = Parse(source: source);
        var analyzer = new SemanticVerifier(language: Language.RazorForge);
        AnalysisResult result = analyzer.Analyze(program: program);

        Assert.Empty(collection: result.Errors);

        var generator = new LlvmCodeGenerator(program: program,
            registry: result.Registry,
            options: new LlvmCodeGeneratorOptions
            {
                StdlibPrograms = result.Registry.StdlibPrograms,
                SynthesizedBodies = result.SynthesizedBodies,
                InstantiatedGenericBodies = result.InstantiatedGenericBodies
            });

        string llvmIr = generator.Generate();
        Assert.Contains(expectedSubstring: "PriorityQueue", actualString: llvmIr);
    }

    /// <summary>
    /// Verifies code generation behavior for nested owned list literal and emits the expected IR.
    /// </summary>
    [Fact]
    public void Codegen_NestedOwnedListLiteral_GeneratesIr()
    {
        string source = """
                        routine test()
                          var items = [[1_s64, 2_s64, 3_s64], [4_s64, 5_s64], [6_s64], []]
                          return

                        routine start()
                          test()
                          return
                        """;

        Program program = Parse(source: source);
        var analyzer = new SemanticVerifier(language: Language.RazorForge);
        AnalysisResult result = analyzer.Analyze(program: program);

        Assert.Empty(collection: result.Errors);

        RoutineDeclaration routine = program.Declarations
                                            .OfType<RoutineDeclaration>()
                                            .Single(predicate: declaration =>
                                                 declaration.Name == "test");
        BlockStatement body = Assert.IsType<BlockStatement>(@object: routine.Body);
        VariableDeclaration variable = body.Statements
                                           .OfType<DeclarationStatement>()
                                           .Select(selector: statement => statement.Declaration)
                                           .OfType<VariableDeclaration>()
                                           .Single(predicate: declaration =>
                                                declaration.Name == "items");

        Assert.Equal(expected: "Core.List[Core.List[Core.S64]]",
            actual: variable.Initializer?.ResolvedType?.FullName);

        var generator = new LlvmCodeGenerator(program: program,
            registry: result.Registry,
            options: new LlvmCodeGeneratorOptions
            {
                StdlibPrograms = result.Registry.StdlibPrograms,
                SynthesizedBodies = result.SynthesizedBodies,
                InstantiatedGenericBodies = result.InstantiatedGenericBodies
            });

        string llvmIr = generator.Generate();
        Assert.Contains(expectedSubstring: "Core.List[Core.List[Core.S64]].create",
            actualString: llvmIr);
    }

    /// <summary>
    /// Regression lock ([[generic-parameter identity = SLOT]]): a user type NAMED like a generic
    /// parameter must never be confused with that parameter. `record T` shares its name with the `T`
    /// in the universal `routine T.represent()` derive AND the numeric `common routine T.to_width()` /
    /// `T.bit()` width helpers. Before the fix the type-level receiver `T` in `T.to_width(...)` resolved
    /// (global lookup before the in-scope-parameter check) to the user record, so the call bound to
    /// `record-T.to_width` and codegen emitted garbage (`zext i64 to record-T` → GetLlvmType crash,
    /// `shl i256 <record-T>`). Driving codegen to completion is the lock — Generate() must not throw.
    /// </summary>
    [Fact]
    public void Codegen_UserRecordNamedLikeGenericParam_DoesNotHijackParam()
    {
        string source = """
                        record T
                          a: S32

                        routine start()
                          var s = T(a: 9_s32).represent()
                          var e = (1.0_d128).erf()
                          return
                        """;

        Program program = Parse(source: source);
        var analyzer = new SemanticVerifier(language: Language.RazorForge);
        AnalysisResult result = analyzer.Analyze(program: program);

        Assert.Empty(collection: result.Errors);

        var generator = new LlvmCodeGenerator(program: program,
            registry: result.Registry,
            options: new LlvmCodeGeneratorOptions
            {
                StdlibPrograms = result.Registry.StdlibPrograms,
                SynthesizedBodies = result.SynthesizedBodies,
                InstantiatedGenericBodies = result.InstantiatedGenericBodies
            });

        // Before the fix this threw: `GenericParameterTypeInfo 'T' reached GetLlvmType` (the numeric
        // helper monomorphized `T.to_width` onto the user record `T`).
        string llvmIr = generator.Generate();

        Assert.Contains(expectedSubstring: "define", actualString: llvmIr);
        // The numeric width helper must key on the real int types, never the user record `T`: a
        // `.T.to_width` (record-owned) symbol is the shadow-hijack signature.
        Assert.DoesNotContain(expectedSubstring: ".T.to_width", actualString: llvmIr);
    }

    /// <summary>
    /// Verifies code generation behavior for try floordiv variant uses failable operator symbols.
    /// </summary>
    [Fact]
    public void Codegen_TryFloordivVariant_UsesFailableOperatorSymbols()
    {
        string source = """
                        routine start()
                          var value = 7_s32.try_floordiv(2_s32)
                          return
                        """;

        Program program = Parse(source: source);
        var analyzer = new SemanticVerifier(language: Language.RazorForge);
        AnalysisResult result = analyzer.Analyze(program: program);

        Assert.Empty(collection: result.Errors);

        var generator = new LlvmCodeGenerator(program: program,
            registry: result.Registry,
            options: new LlvmCodeGeneratorOptions
            {
                StdlibPrograms = result.Registry.StdlibPrograms,
                SynthesizedBodies = result.SynthesizedBodies,
                InstantiatedGenericBodies = result.InstantiatedGenericBodies
            });

        string llvmIr = generator.Generate();
        // Wired-ness is NOT part of the mangled symbol name (it is a routine PROPERTY, not an
        // overload axis) — the attribute prefix carries only crashable/member here.
        Assert.Contains(expectedSubstring: "\"[crashable, member] Core.S32.sub(you: Core.S32)\"",
            actualString: llvmIr);
        Assert.Contains(expectedSubstring: "\"[crashable, member] Core.S32.add(you: Core.S32)\"",
            actualString: llvmIr);
        Assert.DoesNotContain(
            expectedSubstring: "declare void @\"[crashable, member] Core.S32.sub",
            actualString: llvmIr);
        Assert.DoesNotContain(
            expectedSubstring: "declare void @\"[crashable, member] Core.S32.add",
            actualString: llvmIr);
    }

    /// <summary>
    /// Verifies code generation behavior for lambda lift and emits the expected IR.
    /// </summary>
    [Fact]
    public void Codegen_LambdaLift_GeneratesIr()
    {
        // RazorForge has no module-level mutable state (RF-S435), so the captured binding is a
        // routine local — the lambda lift still fires on the closure over `factor`. The lambda
        // parameter `x` needs a type annotation (RF-S638: no body back-inference); a single typed
        // param with a `given` capture must be parenthesized.
        string source = """
                        routine test() -> S32
                          var factor = 100_s32
                          var scale = (x: S32) given factor => x * factor
                          return 0_s32
                        """;

        Program program = Parse(source: source);
        var analyzer = new SemanticVerifier(language: Language.RazorForge);
        AnalysisResult result = analyzer.Analyze(program: program);

        Assert.Empty(collection: result.Errors);
        Assert.False(condition: ContainsLambda(program: program));

        var generator = new LlvmCodeGenerator(program: program,
            registry: result.Registry,
            options: new LlvmCodeGeneratorOptions
            {
                StdlibPrograms = result.Registry.StdlibPrograms,
                SynthesizedBodies = result.SynthesizedBodies,
                InstantiatedGenericBodies = result.InstantiatedGenericBodies
            });

        string llvmIr = generator.Generate();
        Assert.Contains(expectedSubstring: "__lambda_", actualString: llvmIr);
    }

    /// <summary>
    /// Verifies code generation behavior for stdlib bit list add last and emits the expected definition.
    /// </summary>
    [Fact]
    public void Codegen_StdlibBitListAddLast_IsDefined()
    {
        string source = """
                        import Collections.BitList

                        routine test()
                          var bits = BitList()
                          bits.add_last(true)
                          return

                        routine start()
                          test()
                          return
                        """;

        Program program = Parse(source: source);
        var analyzer = new SemanticVerifier(language: Language.RazorForge);
        AnalysisResult result = analyzer.Analyze(program: program);

        Assert.Empty(collection: result.Errors);

        var generator = new LlvmCodeGenerator(program: program,
            registry: result.Registry,
            options: new LlvmCodeGeneratorOptions
            {
                StdlibPrograms = result.Registry.StdlibPrograms,
                SynthesizedBodies = result.SynthesizedBodies,
                InstantiatedGenericBodies = result.InstantiatedGenericBodies
            });

        string llvmIr = generator.Generate();
        Assert.Contains(expectedSubstring: "define void @\"[member] Collections.BitList.add_last(",
            actualString: llvmIr);
    }

    /// <summary>
    /// Verifies code generation behavior for byte size allocator wrapper passes scalar abi to raw c function.
    /// </summary>
    [Fact]
    public void Codegen_ByteSizeAllocatorWrapper_PassesScalarAbiToRawCFunction()
    {
        string source = """
                        import Collections.List

                        routine start()
                          var items = List[S64]()
                          return
                        """;

        Program program = Parse(source: source);
        var analyzer = new SemanticVerifier(language: Language.RazorForge);
        AnalysisResult result = analyzer.Analyze(program: program);

        Assert.Empty(collection: result.Errors);

        var generator = new LlvmCodeGenerator(program: program,
            registry: result.Registry,
            options: new LlvmCodeGeneratorOptions
            {
                StdlibPrograms = result.Registry.StdlibPrograms,
                SynthesizedBodies = result.SynthesizedBodies,
                InstantiatedGenericBodies = result.InstantiatedGenericBodies
            });

        string llvmIr = generator.Generate();
        Assert.Contains(expectedSubstring: "call i64 @rf_allocate_dynamic_uninit(i64 ",
            actualString: llvmIr);
        Assert.DoesNotContain(expectedSubstring: "call i64 @rf_allocate_dynamic_uninit({ i64 }",
            actualString: llvmIr);
        Assert.DoesNotContain(
            expectedSubstring: "call i64 @rf_allocate_dynamic_uninit(%Record.Core.ByteSize",
            actualString: llvmIr);
    }

    /// <summary>
    /// Verifies code generation behavior for concrete LLVM intrinsic call substitutes template type arguments.
    /// </summary>
    [Fact]
    public void Codegen_ConcreteLlvmIntrinsicCall_SubstitutesTemplateTypeArguments()
    {
        string source = """
                        routine test() -> S64
                          return 1_s64 +% 2_s64

                        routine start()
                          discard test()
                          return
                        """;

        Program program = Parse(source: source);
        var analyzer = new SemanticVerifier(language: Language.RazorForge);
        AnalysisResult result = analyzer.Analyze(program: program);

        Assert.Empty(collection: result.Errors);

        var generator = new LlvmCodeGenerator(program: program,
            registry: result.Registry,
            options: new LlvmCodeGeneratorOptions
            {
                StdlibPrograms = result.Registry.StdlibPrograms,
                SynthesizedBodies = result.SynthesizedBodies,
                InstantiatedGenericBodies = result.InstantiatedGenericBodies
            });

        string llvmIr = generator.Generate();
        Assert.Contains(expectedSubstring: "add i64", actualString: llvmIr);
        Assert.DoesNotContain(expectedSubstring: "add {T}", actualString: llvmIr);
        Assert.DoesNotContain(expectedSubstring: "{From}", actualString: llvmIr);
        Assert.DoesNotContain(expectedSubstring: "{To}", actualString: llvmIr);
    }

    /// <summary>
    /// Verifies const generic LLVM intrinsic arguments substitute numeric values, not carrier types.
    /// </summary>
    [Fact]
    public void Codegen_ConstGenericLlvmIntrinsicCall_SubstitutesConstValue()
    {
        string source = """
                        routine test() -> Array[Byte, 8]
                          return 1_u64.to_bytes_le()

                        routine start()
                          discard test()
                          return
                        """;

        Program program = Parse(source: source);
        var analyzer = new SemanticVerifier(language: Language.RazorForge);
        AnalysisResult result = analyzer.Analyze(program: program);

        Assert.Empty(collection: result.Errors);

        var generator = new LlvmCodeGenerator(program: program,
            registry: result.Registry,
            options: new LlvmCodeGeneratorOptions
            {
                StdlibPrograms = result.Registry.StdlibPrograms,
                SynthesizedBodies = result.SynthesizedBodies,
                InstantiatedGenericBodies = result.InstantiatedGenericBodies
            });

        string llvmIr = generator.Generate();
        string body = ExtractFunctionDefinition(llvmIr: llvmIr,
            functionMarker: "define [8 x i8] @\"[member] Core.U64.to_bytes_le");
        Assert.Contains(expectedSubstring: "alloca [8 x i8]", actualString: body);
        Assert.DoesNotContain(expectedSubstring: "[i64 x i8]", actualString: llvmIr);
    }

    /// <summary>
    /// Verifies code generation behavior for bit list to U8 uses concrete hijacked U64 extract.
    /// </summary>
    [Fact]
    public void Codegen_BitListToU8_UsesConcreteHijackedU64Extract()
    {
        // Variants are synthesized + emitted ON DEMAND — a bare propagating `to_u8!()` never
        // materializes `try_to_u8`. The `trigger` routine RECOVERS via the `try_` variant so the
        // body this test inspects is actually generated (mirrors ErrorVariantGenerationTests).
        string source = """
                        import Collections.BitList

                        routine test(bits: BitList) -> U8!
                          return bits.to_u8!()

                        routine trigger(bits: BitList) -> U8
                          discard bits.try_to_u8()
                          return 0u8

                        routine start()
                          var bits = BitList()
                          discard trigger(bits: steal bits)
                          return
                        """;

        Program program = Parse(source: source);
        var analyzer = new SemanticVerifier(language: Language.RazorForge);
        AnalysisResult result = analyzer.Analyze(program: program);

        Assert.Empty(collection: result.Errors);

        var generator = new LlvmCodeGenerator(program: program,
            registry: result.Registry,
            options: new LlvmCodeGeneratorOptions
            {
                StdlibPrograms = result.Registry.StdlibPrograms,
                SynthesizedBodies = result.SynthesizedBodies,
                InstantiatedGenericBodies = result.InstantiatedGenericBodies
            });

        string llvmIr = generator.Generate();
        string tryToU8Body = ExtractFunctionDefinition(llvmIr: llvmIr,
            functionMarker:
            "define internal %\"Record.Core.Maybe[Core.U8]\" @\"[member] Collections.BitList.try_to_u8");
        Assert.Contains(
            expectedSubstring: "call i64 @\"[dangerous, member] Core.Hijacked[Core.U64].peek()\"",
            actualString: tryToU8Body);
        Assert.DoesNotContain(
            expectedSubstring: "@\"[dangerous, member] Core.Hijacked[Core.Bytes].peek()\"",
            actualString: tryToU8Body);
        Assert.DoesNotContain(expectedSubstring: "Core.Bytes.bitand", actualString: tryToU8Body);
    }

    /// <summary>
    /// Verifies code generation behavior for overloaded try create variants get distinct mangled names.
    /// </summary>
    [Fact]
    public void Codegen_OverloadedTryCreateVariants_GetDistinctMangledNames()
    {
        string source = """
                        routine test()
                          return
                        """;

        Program program = Parse(source: source);
        var analyzer = new SemanticVerifier(language: Language.RazorForge);
        AnalysisResult result = analyzer.Analyze(program: program);

        Assert.Empty(collection: result.Errors);

        TypeInfo? s64Type = result.Registry.LookupType(name: "S64");
        TypeInfo? s8Type = result.Registry.LookupType(name: "S8");
        TypeInfo? textType = result.Registry.LookupType(name: "Text");
        TypeInfo? maybeDef = result.Registry.LookupType(name: "Maybe");
        Assert.NotNull(@object: s64Type);
        Assert.NotNull(@object: s8Type);
        Assert.NotNull(@object: textType);
        Assert.NotNull(@object: maybeDef);
        TypeInfo maybeS64 = result.Registry.GetOrCreateResolution(genericDef: maybeDef,
            typeArguments: [s64Type]);

        string fromS8 = LlvmCodeGenerator.MangleRoutineName(
            routine: new RoutineInfo(name: "try_create")
            {
                OwnerType = s64Type,
                Parameters = [new ParameterInfo(name: "from", type: s8Type)],
                ReturnType = maybeS64,
                OriginalName = "$create",
                IsSynthesized = true
            });

        string fromText = LlvmCodeGenerator.MangleRoutineName(
            routine: new RoutineInfo(name: "try_create")
            {
                OwnerType = s64Type,
                Parameters = [new ParameterInfo(name: "from_text", type: textType)],
                ReturnType = maybeS64,
                OriginalName = "$create",
                IsSynthesized = true
            });

        Assert.Equal(expected: "\"[member] Core.S64.try_create(from: Core.S8)\"", actual: fromS8);
        Assert.Equal(expected: "\"[member] Core.S64.try_create(from_text: Core.Text)\"",
            actual: fromText);
        Assert.NotEqual(expected: fromS8, actual: fromText);
    }

    /// <summary>
    /// Verifies code generation behavior for generic hijack does not emit bare create symbol.
    /// </summary>
    [Fact]
    public void Codegen_GenericHijack_DoesNotEmitBareCreateSymbol()
    {
        string source = """
                        dangerous routine wrap[T](value: T) -> Hijacked[T]
                          return value.hijack()

                        dangerous routine start(value: S64) -> Hijacked[S64]
                          return wrap[S64](value)
                        """;

        Program program = Parse(source: source);
        var analyzer = new SemanticVerifier(language: Language.RazorForge);
        AnalysisResult result = analyzer.Analyze(program: program);

        Assert.Empty(collection: result.Errors);

        var generator = new LlvmCodeGenerator(program: program,
            registry: result.Registry,
            options: new LlvmCodeGeneratorOptions
            {
                StdlibPrograms = result.Registry.StdlibPrograms,
                SynthesizedBodies = result.SynthesizedBodies,
                InstantiatedGenericBodies = result.InstantiatedGenericBodies
            });

        string llvmIr = generator.Generate();
        Assert.DoesNotContain(expectedSubstring: "call void @$create", actualString: llvmIr);
        Assert.DoesNotContain(expectedSubstring: "call ptr @$create", actualString: llvmIr);
    }

    /// <summary>
    /// Verifies code generation behavior for generic hijacked from does not emit bare module symbol.
    /// </summary>
    [Fact]
    public void Codegen_GenericHijackedFrom_DoesNotEmitBareModuleSymbol()
    {
        string source = """
                        dangerous routine start() -> Hijacked[S64]
                          return hijacked_from[S64](0_addr)
                        """;

        Program program = Parse(source: source);
        var analyzer = new SemanticVerifier(language: Language.RazorForge);
        AnalysisResult result = analyzer.Analyze(program: program);

        Assert.Empty(collection: result.Errors);

        var generator = new LlvmCodeGenerator(program: program,
            registry: result.Registry,
            options: new LlvmCodeGeneratorOptions
            {
                StdlibPrograms = result.Registry.StdlibPrograms,
                SynthesizedBodies = result.SynthesizedBodies,
                InstantiatedGenericBodies = result.InstantiatedGenericBodies
            });

        string llvmIr = generator.Generate();
        Assert.DoesNotContain(expectedSubstring: "call ptr @Core.hijacked_from(",
            actualString: llvmIr);
        Assert.Contains(
            expectedSubstring:
            "define ptr @\"[independent] Core.hijacked_from(S64)(addr: Core.Address)\"(i64 %addr)",
            actualString: llvmIr);
    }

    /// <summary>
    /// Verifies code generation behavior for chained memberRoutine call on call receiver and emits the expected IR.
    /// </summary>
    [Fact]
    public void Codegen_ChainedMemberRoutineCall_OnCallReceiver_GeneratesIr()
    {
        string source = """
                        record Box[T]
                          value: T

                        routine Box[T].peek() -> T
                          return me.value

                        routine make_box() -> Box[S32]
                          return Box[S32](value: 7_s32)

                        routine test() -> S32
                          return make_box().peek()
                        """;

        Program program = Parse(source: source);
        var analyzer = new SemanticVerifier(language: Language.RazorForge);
        AnalysisResult result = analyzer.Analyze(program: program);

        Assert.Empty(collection: result.Errors);

        var generator = new LlvmCodeGenerator(program: program,
            registry: result.Registry,
            options: new LlvmCodeGeneratorOptions
            {
                StdlibPrograms = result.Registry.StdlibPrograms,
                SynthesizedBodies = result.SynthesizedBodies,
                InstantiatedGenericBodies = result.InstantiatedGenericBodies
            });

        string llvmIr = generator.Generate();
        Assert.Contains(expectedSubstring: "define i32 @\"[independent] test()\"",
            actualString: llvmIr);
        Assert.Contains(expectedSubstring: "@\"[member] Box[Core.S32].peek()\"",
            actualString: llvmIr);
    }

    /// <summary>
    /// Verifies code generation behavior for wrapper memberRoutine call uses concrete generic return type.
    /// </summary>
    [Fact]
    public void Codegen_WrapperMemberRoutineCall_UsesConcreteGenericReturnType()
    {
        string source = """
                        dangerous routine test(ptr: Hijacked[S64]) -> S64
                          return ptr.peek()
                        """;

        Program program = Parse(source: source);
        var analyzer = new SemanticVerifier(language: Language.RazorForge);
        AnalysisResult result = analyzer.Analyze(program: program);

        Assert.Empty(collection: result.Errors);

        var generator = new LlvmCodeGenerator(program: program,
            registry: result.Registry,
            options: new LlvmCodeGeneratorOptions
            {
                StdlibPrograms = result.Registry.StdlibPrograms,
                SynthesizedBodies = result.SynthesizedBodies,
                InstantiatedGenericBodies = result.InstantiatedGenericBodies
            });

        string llvmIr = generator.Generate();
        Assert.Contains(
            expectedSubstring:
            "define i64 @\"[dangerous, independent] test(ptr: Core.Hijacked[Core.S64])\"(ptr %ptr)",
            actualString: llvmIr);
        Assert.Contains(
            expectedSubstring: "@\"[dangerous, member] Core.Hijacked[Core.S64].peek()\"",
            actualString: llvmIr);
    }

    /// <summary>
    /// Verifies code generation behavior for memberRoutine conversion call uses semantic return type.
    /// </summary>
    [Fact]
    public void Codegen_memberRoutineConversionCall_UsesSemanticReturnType()
    {
        string source = """
                        routine helper(value: S32) -> S32
                          return value

                        routine test(text: Text) -> S32
                          return helper(text.count().S32())

                        routine start()
                          discard test(text: "")
                          return
                        """;

        Program program = Parse(source: source);
        var analyzer = new SemanticVerifier(language: Language.RazorForge);
        AnalysisResult result = analyzer.Analyze(program: program);

        Assert.Empty(collection: result.Errors);

        var generator = new LlvmCodeGenerator(program: program,
            registry: result.Registry,
            options: new LlvmCodeGeneratorOptions
            {
                StdlibPrograms = result.Registry.StdlibPrograms,
                SynthesizedBodies = result.SynthesizedBodies,
                InstantiatedGenericBodies = result.InstantiatedGenericBodies
            });

        string llvmIr = generator.Generate();
        Assert.Contains(expectedSubstring: "define i32 @\"[independent] test(text: Core.Text)\"(",
            actualString: llvmIr);
        Assert.Contains(expectedSubstring: "trunc i64", actualString: llvmIr);
        Assert.Contains(
            expectedSubstring: "call i32 @\"[independent] helper(value: Core.S32)\"(i32 ",
            actualString: llvmIr);
    }

    /// <summary>
    /// Verifies semantic analysis behavior for stdlib variant bodies attach constructor metadata.
    /// </summary>
    [Fact]
    public void Analyze_StdlibVariantBodies_AttachConstructorMetadata()
    {
        string source = """
                        routine test() -> None
                          return
                        """;

        Program program = Parse(source: source);
        var analyzer = new SemanticVerifier(language: Language.RazorForge);
        AnalysisResult result = analyzer.Analyze(program: program);

        Assert.Empty(collection: result.Errors);

        var matchingBodies = result.SynthesizedBodies
                                   .Where(predicate: pair =>
                                        pair.Key.Contains(value: "BytesUtf8Emittable.try_emit",
                                            comparisonType: StringComparison.Ordinal) ||
                                        pair.Key.Contains(value: "BytesUtf8Emittable.lookup_emit",
                                            comparisonType: StringComparison.Ordinal))
                                   .ToList();

        Assert.NotEmpty(collection: matchingBodies);
        foreach ((string key, Statement body) in matchingBodies)
        {
            // The lowering pipeline may leave Character(...) as a classified CallExpression
            // or lower it to a CreatorExpression — both must carry constructor metadata.
            var constructions = EnumerateExpressions(statement: body)
                               .Where(predicate: e => e is CallExpression
                                {
                                    Callee: IdentifierExpression { Name: "Character" }
                                } or CreatorExpression { TypeName: "Character" })
                               .ToList();

            Assert.NotEmpty(collection: constructions);
            Assert.All(collection: constructions,
                action: expr =>
                {
                    switch (expr)
                    {
                        case CallExpression ctorCall:
                            Assert.True(
                                condition: ctorCall.LoweringKind ==
                                           CallLoweringKind.TypeConstructor,
                                userMessage: $"{key} contains unclassified Character(...)");
                            Assert.NotNull(@object: ctorCall.ConstructedType);
                            break;
                        case CreatorExpression creator:
                            Assert.NotNull(@object: creator.ConstructedType);
                            break;
                    }
                });
        }
    }

    /// <summary>
    /// Verifies code generation behavior for stdlib variant bodies do not warn about missing character constructor metadata.
    /// </summary>
    [Fact]
    public void Codegen_StdlibVariantBodies_DoNotWarnAboutMissingCharacterConstructorMetadata()
    {
        string source = """
                        routine helper(value: S32) -> S32
                          return value

                        routine test!(text: Text) -> S32
                          return helper(text.count().S32!())
                        """;

        Program program = Parse(source: source);
        var analyzer = new SemanticVerifier(language: Language.RazorForge);
        AnalysisResult result = analyzer.Analyze(program: program);

        Assert.Empty(collection: result.Errors);

        var generator = new LlvmCodeGenerator(program: program,
            registry: result.Registry,
            options: new LlvmCodeGeneratorOptions
            {
                StdlibPrograms = result.Registry.StdlibPrograms,
                SynthesizedBodies = result.SynthesizedBodies,
                InstantiatedGenericBodies = result.InstantiatedGenericBodies
            });

        TextWriter originalError = Console.Error;
        var errorWriter = new StringWriter();
        Console.SetError(newError: errorWriter);
        try
        {
            _ = generator.Generate();
        }
        finally
        {
            Console.SetError(newError: originalError);
        }

        string warnings = errorWriter.ToString();
        Assert.DoesNotContain(
            expectedSubstring:
            "Warning: Synthesized codegen failed for 'Core.BytesUtf8Emittable.try_emit'",
            actualString: warnings);
        Assert.DoesNotContain(
            expectedSubstring:
            "Warning: Synthesized codegen failed for 'Core.BytesUtf8Emittable.lookup_emit'",
            actualString: warnings);
    }

    /// <summary>
    /// Verifies backend entry validation behavior for entry validator rejects residual preset identifier.
    /// </summary>
    [Fact]
    public void BackendEntryValidator_RejectsResidualPresetIdentifier()
    {
        string source = """
                        preset LIMIT: S64 = 10_s64

                        routine test() -> S64
                          return LIMIT
                        """;

        Program program = Parse(source: source);
        var analyzer = new SemanticVerifier(language: Language.RazorForge);
        AnalysisResult result = analyzer.Analyze(program: program);

        Assert.Empty(collection: result.Errors);

        var leakedPreset = new ReturnStatement(
            Value: new IdentifierExpression(Name: "LIMIT", Location: program.Location)
            {
                ResolvedType = result.Registry.LookupType(name: "S64")
            },
            Location: program.Location);

        var validator = new BackendEntryValidator(registry: result.Registry);
        IReadOnlyList<SemanticError> errors = validator.ValidateStatement(statement: leakedPreset);
        Assert.Contains(collection: errors,
            filter: error => error.Code == SemanticDiagnosticCode.IllegalBackendPresetIdentifier);
    }

    /// <summary>
    /// Verifies backend entry validation behavior for entry validator rejects constructor like call without lowering metadata.
    /// </summary>
    [Fact]
    public void BackendEntryValidator_RejectsConstructorLikeCallWithoutLoweringMetadata()
    {
        string source = """
                        routine test() -> Character
                          return Character(65_u32)
                        """;

        Program program = Parse(source: source);
        var analyzer = new SemanticVerifier(language: Language.RazorForge);
        AnalysisResult result = analyzer.Analyze(program: program);

        Assert.Empty(collection: result.Errors);

        TypeInfo characterType = Assert.IsType<RecordTypeInfo>(
            @object: result.Registry.LookupType(name: "Character"));
        var leakedCall = new CallExpression(
            Callee: new IdentifierExpression(Name: "Character", Location: program.Location),
            Arguments:
            [
                new LiteralExpression(Value: "65_u32",
                    LiteralType: Compiler.Tokenizer.TokenType.U32Literal,
                    Location: program.Location)
            ],
            Location: program.Location) { ResolvedType = characterType };

        var leakedReturn = new ReturnStatement(Value: leakedCall, Location: program.Location);
        var validator = new BackendEntryValidator(registry: result.Registry);
        IReadOnlyList<SemanticError> errors = validator.ValidateStatement(statement: leakedReturn);
        Assert.Contains(collection: errors,
            filter: error => error.Code == SemanticDiagnosticCode.MissingCallLoweringMetadata);
    }

    /// <summary>
    /// Verifies backend entry validation behavior for entry validator rejects residual index without concrete type.
    /// </summary>
    [Fact]
    public void BackendEntryValidator_RejectsResidualIndexWithoutConcreteType()
    {
        string source = """
                        routine test() -> None
                          return
                        """;

        Program program = Parse(source: source);
        var analyzer = new SemanticVerifier(language: Language.RazorForge);
        AnalysisResult result = analyzer.Analyze(program: program);

        Assert.Empty(collection: result.Errors);

        var leakedIndex = new IndexExpression(
            Object: new IdentifierExpression(Name: "items", Location: program.Location),
            Index: new LiteralExpression(Value: "0_s64",
                LiteralType: Compiler.Tokenizer.TokenType.S64Literal,
                Location: program.Location),
            Location: program.Location) { ResolvedType = new GenericParameterTypeInfo(name: "T") };

        var leakedReturn = new ReturnStatement(Value: leakedIndex, Location: program.Location);
        var validator = new BackendEntryValidator(registry: result.Registry);
        IReadOnlyList<SemanticError> errors = validator.ValidateStatement(statement: leakedReturn);
        Assert.Contains(collection: errors,
            filter: error => error.Code == SemanticDiagnosticCode.UnresolvedBackendGeneric);
    }

    /// <summary>
    /// Verifies semantic analysis behavior for dict index call attaches concrete value type.
    /// </summary>
    [Fact]
    public void Analyze_DictIndexCall_AttachesConcreteValueType()
    {
        string source = """
                        routine test(dict: Dict[S64, S64], key: S64) -> S64
                          return dict[key]
                        """;

        Program program = Parse(source: source);
        // SA-only: keep the body un-lowered so `dict[key]` stays an IndexExpression whose value
        // type SA resolves from the concrete Dict[S64, S64] parameter (lowering would otherwise
        // hoist it into a temp-var decl, hiding the resolved type behind an identifier).
        var analyzer = new SemanticVerifier(language: Language.RazorForge) { SaOnly = true };
        AnalysisResult result = analyzer.Analyze(program: program);

        Assert.Empty(collection: result.Errors);

        RoutineDeclaration testRoutine = program.Declarations
                                                .OfType<RoutineDeclaration>()
                                                .Single(predicate: declaration =>
                                                     declaration.Name == "test");
        BlockStatement body = Assert.IsType<BlockStatement>(@object: testRoutine.Body);
        ReturnStatement returnStatement =
            Assert.IsType<ReturnStatement>(@object: body.Statements.Single());
        IndexExpression index = Assert.IsType<IndexExpression>(@object: returnStatement.Value);
        TypeInfo resolvedType = index.ResolvedType!;
        Assert.NotNull(@object: resolvedType);
        Assert.Equal(expected: "S64", actual: resolvedType.Name);
        Assert.False(condition: ContainsGenericPlaceholder(type: resolvedType));
    }

    /// <summary>
    /// Verifies backend entry validation behavior for entry validator rejects direct routine call without resolved metadata.
    /// </summary>
    [Fact]
    public void BackendEntryValidator_RejectsDirectRoutineCallWithoutResolvedMetadata()
    {
        string source = """
                        routine helper(value: S32) -> S32
                          return value

                        routine test() -> S32
                          return helper(1_s32)
                        """;

        Program program = Parse(source: source);
        var analyzer = new SemanticVerifier(language: Language.RazorForge);
        AnalysisResult result = analyzer.Analyze(program: program);

        Assert.Empty(collection: result.Errors);

        var leakedCall = new CallExpression(
            Callee: new IdentifierExpression(Name: "helper", Location: program.Location),
            Arguments:
            [
                new LiteralExpression(Value: "1_s32",
                    LiteralType: Compiler.Tokenizer.TokenType.S32Literal,
                    Location: program.Location)
            ],
            Location: program.Location);

        var leakedReturn = new ReturnStatement(Value: leakedCall, Location: program.Location);
        var validator = new BackendEntryValidator(registry: result.Registry);
        IReadOnlyList<SemanticError> errors = validator.ValidateStatement(statement: leakedReturn);
        Assert.Contains(collection: errors,
            filter: error => error.Code == SemanticDiagnosticCode.MissingCallLoweringMetadata);
    }

    /// <summary>
    /// Verifies code generation behavior for const generic preset type argument uses resolved type expression metadata.
    /// </summary>
    [Fact]
    public void Codegen_ConstGenericPresetTypeArgument_UsesResolvedTypeExpressionMetadata()
    {
        string source = """
                        preset WIDTH: Address = 16addr

                        entity Buffer[T, N]
                        needs N is Address
                          data: T

                        routine Buffer[T, N].first() -> T
                          return me.data

                        routine start(buf: Buffer[U8, WIDTH]) -> U8
                          return buf.first()
                        """;

        Program program = Parse(source: source);
        var analyzer = new SemanticVerifier(language: Language.RazorForge);
        AnalysisResult result = analyzer.Analyze(program: program);

        Assert.Empty(collection: result.Errors);

        RoutineDeclaration testRoutine = program.Declarations
                                                .OfType<RoutineDeclaration>()
                                                .Single(predicate: declaration =>
                                                     declaration.Name == "start");
        TypeExpression parameterType =
            Assert.IsType<TypeExpression>(@object: testRoutine.Parameters[index: 0].Type);
        TypeExpression widthArg =
            Assert.IsType<TypeExpression>(@object: parameterType.GenericArguments![index: 1]);
        ConstGenericValueTypeInfo resolvedWidth =
            Assert.IsType<ConstGenericValueTypeInfo>(@object: widthArg.ResolvedType);
        Assert.Equal(expected: 16, actual: resolvedWidth.Value);
        Assert.Equal(expected: "Address", actual: resolvedWidth.ExplicitTypeName);

        var generator = new LlvmCodeGenerator(program: program,
            registry: result.Registry,
            options: new LlvmCodeGeneratorOptions
            {
                StdlibPrograms = result.Registry.StdlibPrograms,
                SynthesizedBodies = result.SynthesizedBodies,
                InstantiatedGenericBodies = result.InstantiatedGenericBodies
            });

        string llvmIr = generator.Generate();
        Assert.Contains(expectedSubstring: "define i8 @\"[independent] start(",
            actualString: llvmIr);
    }

    /// <summary>
    /// Verifies code generation behavior for typewise builder service memberRoutine uses semantic receiver type.
    /// </summary>
    [Fact]
    public void Codegen_TypewiseBuilderQueryMemberRoutine_UsesSemanticReceiverType()
    {
        string source = """
                        import BuilderQuery

                        routine start() -> ByteSize
                          return S64.data_size()
                        """;

        Program program = Parse(source: source);
        var analyzer = new SemanticVerifier(language: Language.RazorForge);
        AnalysisResult result = analyzer.Analyze(program: program);

        Assert.Empty(collection: result.Errors);

        var generator = new LlvmCodeGenerator(program: program,
            registry: result.Registry,
            options: new LlvmCodeGeneratorOptions
            {
                StdlibPrograms = result.Registry.StdlibPrograms,
                SynthesizedBodies = result.SynthesizedBodies,
                InstantiatedGenericBodies = result.InstantiatedGenericBodies
            });

        string llvmIr = generator.Generate();
        // ByteSize is a single-field record flattened to its i64 backing scalar, and
        // S64.data_size() must fold against the SEMANTIC receiver (S64 -> 8 bytes) at
        // the call site rather than emitting a data_size call.
        string startBody = ExtractFunctionDefinition(llvmIr: llvmIr,
            functionMarker: "define i64 @\"[independent] start()\"");
        Assert.Contains(expectedSubstring: "ret i64 8", actualString: startBody);
        Assert.DoesNotContain(expectedSubstring: "call i64 @Core.S64.data_size",
            actualString: startBody);
    }

    /// <summary>
    /// Verifies code generation behavior for monomorphized generic body emits standalone generic helper definition.
    /// </summary>
    [Fact]
    public void Codegen_MonomorphizedGenericBody_EmitsStandaloneGenericHelperDefinition()
    {
        string source = """
                        dangerous routine wrap_addr[T](addr: Address) -> Hijacked[T]
                          return hijacked_from[T](addr)

                        dangerous routine start() -> Hijacked[S64]
                          return wrap_addr[S64](0_addr)
                        """;

        Program program = Parse(source: source);
        var analyzer = new SemanticVerifier(language: Language.RazorForge);
        AnalysisResult result = analyzer.Analyze(program: program);

        Assert.Empty(collection: result.Errors);

        var generator = new LlvmCodeGenerator(program: program,
            registry: result.Registry,
            options: new LlvmCodeGeneratorOptions
            {
                StdlibPrograms = result.Registry.StdlibPrograms,
                SynthesizedBodies = result.SynthesizedBodies,
                InstantiatedGenericBodies = result.InstantiatedGenericBodies
            });

        string llvmIr = generator.Generate();
        Assert.Contains(
            expectedSubstring:
            "define ptr @\"[dangerous, independent] wrap_addr(S64)(addr: Core.Address)\"(i64 %addr)",
            actualString: llvmIr);
        Assert.Contains(
            expectedSubstring:
            "define ptr @\"[independent] Core.hijacked_from(S64)(addr: Core.Address)\"(i64 %addr)",
            actualString: llvmIr);
    }

    /// <summary>
    /// Verifies semantic analysis behavior for universal owner memberRoutine is monomorphized on demand.
    /// </summary>
    [Fact]
    public void Analyze_UniversalOwnerMemberRoutine_IsMonomorphizedOnDemand()
    {
        string source = """
                        dangerous routine start(value: S64) -> Hijacked[S64]
                          return value.hijack()
                        """;

        Program program = Parse(source: source);
        var analyzer = new SemanticVerifier(language: Language.RazorForge);
        AnalysisResult result = analyzer.Analyze(program: program);

        Assert.Empty(collection: result.Errors);

        RoutineInfo resolvedHijack =
            Assert.Single(collection: result.Registry.GetAllRoutineResolutions(),
                predicate: routine => routine.BaseName == "S64.hijack");
        Assert.Contains(collection: result.InstantiatedGenericBodies.Values,
            filter: body => body.Info.RegistryKey == resolvedHijack.RegistryKey &&
                            !ContainsGenericPlaceholder(type: body.Info.ReturnType));
    }

    private static bool ContainsTupleLiteral(Program program)
    {
        return program.Declarations
                      .OfType<RoutineDeclaration>()
                      .Any(predicate: routine => ContainsTupleLiteral(statement: routine.Body));
    }

    private static string ExtractFunctionDefinition(string llvmIr, string functionMarker)
    {
        int start =
            llvmIr.IndexOf(value: functionMarker, comparisonType: StringComparison.Ordinal);
        Assert.True(condition: start >= 0,
            userMessage: $"FreeRoutine marker not found: {functionMarker}");

        int next = llvmIr.IndexOf(value: "\ndefine ",
            startIndex: start + functionMarker.Length,
            comparisonType: StringComparison.Ordinal);
        return next >= 0
            ? llvmIr[start..next]
            : llvmIr[start..];
    }

    private static CallExpression GetReturnedCall(Program program, string routineName)
    {
        RoutineDeclaration routine = program.Declarations
                                            .OfType<RoutineDeclaration>()
                                            .Single(predicate: declaration =>
                                                 declaration.Name == routineName);
        return GetReturnedCall(body: routine.Body);
    }

    private static CallExpression GetReturnedCall(Statement body)
    {
        BlockStatement block = Assert.IsType<BlockStatement>(@object: body);
        ReturnStatement ret = Assert.IsType<ReturnStatement>(@object: block.Statements.Last());
        return Assert.IsType<CallExpression>(@object: ret.Value);
    }

    private static CallExpression GetVariableInitializerCall(Program program, string variableName)
    {
        RoutineDeclaration routine = program.Declarations
                                            .OfType<RoutineDeclaration>()
                                            .Single(predicate: declaration =>
                                                 declaration.Name == "test");
        BlockStatement block = Assert.IsType<BlockStatement>(@object: routine.Body);
        VariableDeclaration variable = block.Statements
                                            .OfType<DeclarationStatement>()
                                            .Select(selector: declaration =>
                                                 declaration.Declaration)
                                            .OfType<VariableDeclaration>()
                                            .Single(predicate: declaration =>
                                                 declaration.Name == variableName);
        return Assert.IsType<CallExpression>(@object: variable.Initializer);
    }

    private static CreatorExpression GetVariableInitializerCreator(Program program,
        string variableName)
    {
        RoutineDeclaration routine = program.Declarations
                                            .OfType<RoutineDeclaration>()
                                            .Single(predicate: declaration =>
                                                 declaration.Name == "test");
        BlockStatement block = Assert.IsType<BlockStatement>(@object: routine.Body);
        VariableDeclaration variable = block.Statements
                                            .OfType<DeclarationStatement>()
                                            .Select(selector: declaration =>
                                                 declaration.Declaration)
                                            .OfType<VariableDeclaration>()
                                            .Single(predicate: declaration =>
                                                 declaration.Name == variableName);
        return Assert.IsType<CreatorExpression>(@object: variable.Initializer);
    }

    private static bool ContainsTupleLiteral(Statement statement)
    {
        return statement switch
        {
            BlockStatement block => block.Statements.Any(predicate: ContainsTupleLiteral),
            DeclarationStatement { Declaration: VariableDeclaration { Initializer: { } init } } =>
                ContainsTupleLiteral(expression: init),
            AssignmentStatement assign => ContainsTupleLiteral(expression: assign.Target) ||
                                          ContainsTupleLiteral(expression: assign.Value),
            ReturnStatement { Value: { } value } => ContainsTupleLiteral(expression: value),
            ExpressionStatement exprStmt => ContainsTupleLiteral(expression: exprStmt.Expression),
            IfStatement ifs => ContainsTupleLiteral(expression: ifs.Condition) ||
                               ContainsTupleLiteral(statement: ifs.ThenStatement) ||
                               ifs.ElseStatement != null &&
                               ContainsTupleLiteral(statement: ifs.ElseStatement),
            LoopStatement loop => ContainsTupleLiteral(statement: loop.Body),
            WhenStatement whenStmt => ContainsTupleLiteral(expression: whenStmt.Expression) ||
                                      whenStmt.Clauses.Any(predicate: clause =>
                                          ContainsTupleLiteral(statement: clause.Body)),
            _ => false
        };
    }

    private static bool ContainsTupleLiteral(Expression expression)
    {
        return expression switch
        {
            TupleLiteralExpression => true,
            CreatorExpression creator => creator.MemberVariables.Any(predicate: mv =>
                ContainsTupleLiteral(expression: mv.Value)),
            CallExpression call => ContainsTupleLiteral(expression: call.Callee) ||
                                   call.Arguments.Any(predicate: ContainsTupleLiteral),
            BinaryExpression binary => ContainsTupleLiteral(expression: binary.Left) ||
                                       ContainsTupleLiteral(expression: binary.Right),
            UnaryExpression unary => ContainsTupleLiteral(expression: unary.Operand),
            MemberExpression member => ContainsTupleLiteral(expression: member.Object),
            ConditionalExpression conditional =>
                ContainsTupleLiteral(expression: conditional.Condition) ||
                ContainsTupleLiteral(expression: conditional.TrueExpression) ||
                ContainsTupleLiteral(expression: conditional.FalseExpression),
            _ => false
        };
    }

    private static bool ContainsBecomes(Program program)
    {
        return program.Declarations
                      .OfType<RoutineDeclaration>()
                      .Any(predicate: routine => ContainsBecomes(statement: routine.Body));
    }

    private static bool ContainsLambda(Program program)
    {
        return program.Declarations
                      .OfType<RoutineDeclaration>()
                      .Any(predicate: routine => ContainsLambda(statement: routine.Body));
    }

    private static bool ContainsBecomes(Statement statement)
    {
        return statement switch
        {
            BecomesStatement => true,
            BlockStatement block => block.Statements.Any(predicate: ContainsBecomes),
            IfStatement ifs => ContainsBecomes(statement: ifs.ThenStatement) ||
                               ifs.ElseStatement != null &&
                               ContainsBecomes(statement: ifs.ElseStatement),
            LoopStatement loop => ContainsBecomes(statement: loop.Body),
            WhenStatement whenStmt => whenStmt.Clauses.Any(predicate: clause =>
                ContainsBecomes(statement: clause.Body)),
            DangerStatement danger => ContainsBecomes(statement: danger.Body),
            UsingStatement usingStmt => ContainsBecomes(statement: usingStmt.Body),
            _ => false
        };
    }

    private static bool ContainsLambda(Statement statement)
    {
        return statement switch
        {
            BlockStatement block => block.Statements.Any(predicate: ContainsLambda),
            DeclarationStatement { Declaration: VariableDeclaration { Initializer: { } init } } =>
                ContainsLambda(expression: init),
            AssignmentStatement assign => ContainsLambda(expression: assign.Target) ||
                                          ContainsLambda(expression: assign.Value),
            ReturnStatement { Value: { } value } => ContainsLambda(expression: value),
            ExpressionStatement exprStmt => ContainsLambda(expression: exprStmt.Expression),
            IfStatement ifs => ContainsLambda(expression: ifs.Condition) ||
                               ContainsLambda(statement: ifs.ThenStatement) ||
                               ifs.ElseStatement != null &&
                               ContainsLambda(statement: ifs.ElseStatement),
            WhileStatement whileStmt => ContainsLambda(expression: whileStmt.Condition) ||
                                        ContainsLambda(statement: whileStmt.Body) ||
                                        whileStmt.ElseBranch != null &&
                                        ContainsLambda(statement: whileStmt.ElseBranch),
            LoopStatement loop => ContainsLambda(statement: loop.Body),
            EachStatement eachStmt => ContainsLambda(expression: eachStmt.Iterable) ||
                                      ContainsLambda(statement: eachStmt.Body) ||
                                      eachStmt.ElseBranch != null &&
                                      ContainsLambda(statement: eachStmt.ElseBranch),
            WhenStatement whenStmt => ContainsLambda(expression: whenStmt.Expression) ||
                                      whenStmt.Clauses.Any(predicate: clause =>
                                          ContainsLambda(statement: clause.Body)),
            DangerStatement danger => ContainsLambda(statement: danger.Body),
            UsingStatement usingStmt => ContainsLambda(expression: usingStmt.Resource) ||
                                        ContainsLambda(statement: usingStmt.Body),
            DiscardStatement discard => ContainsLambda(expression: discard.Expression),
            ThrowStatement throwStmt => ContainsLambda(expression: throwStmt.Error),
            BecomesStatement becomes => ContainsLambda(expression: becomes.Value),
            _ => false
        };
    }

    private static bool ContainsLambda(Expression expression)
    {
        return expression switch
        {
            LambdaExpression => true,
            BinaryExpression binary => ContainsLambda(expression: binary.Left) ||
                                       ContainsLambda(expression: binary.Right),
            UnaryExpression unary => ContainsLambda(expression: unary.Operand),
            CallExpression call => ContainsLambda(expression: call.Callee) ||
                                   call.Arguments.Any(predicate: ContainsLambda),
            MemberExpression member => ContainsLambda(expression: member.Object),
            OptionalMemberExpression member => ContainsLambda(expression: member.Object),
            IndexExpression index => ContainsLambda(expression: index.Object) ||
                                     ContainsLambda(expression: index.Index),
            ConditionalExpression conditional =>
                ContainsLambda(expression: conditional.Condition) ||
                ContainsLambda(expression: conditional.TrueExpression) ||
                ContainsLambda(expression: conditional.FalseExpression),
            CreatorExpression creator => creator.MemberVariables.Any(predicate: mv =>
                ContainsLambda(expression: mv.Value)),
            GenericMemberRoutineCallExpression generic =>
                ContainsLambda(expression: generic.Object) ||
                generic.Arguments.Any(predicate: ContainsLambda),
            NamedArgumentExpression named => ContainsLambda(expression: named.Value),
            WithExpression withExpr => ContainsLambda(expression: withExpr.Base) ||
                                       withExpr.Updates.Any(predicate: update =>
                                           ContainsLambda(expression: update.Value) ||
                                           update.Index != null &&
                                           ContainsLambda(expression: update.Index)),
            ListLiteralExpression list => list.Elements.Any(predicate: ContainsLambda),
            SetLiteralExpression set => set.Elements.Any(predicate: ContainsLambda),
            DictLiteralExpression dict => dict.Pairs.Any(predicate: pair =>
                ContainsLambda(expression: pair.Key) || ContainsLambda(expression: pair.Value)),
            TupleLiteralExpression tuple => tuple.Elements.Any(predicate: ContainsLambda),
            TypeConversionExpression conversion => ContainsLambda(
                expression: conversion.Expression),
            ChainedComparisonExpression chained => chained.Operands.Any(predicate: ContainsLambda),
            BlockExpression block => ContainsLambda(expression: block.Value),
            DictEntryLiteralExpression dictEntry => ContainsLambda(expression: dictEntry.Key) ||
                                                    ContainsLambda(expression: dictEntry.Value),
            IsPatternExpression isPattern => ContainsLambda(expression: isPattern.Expression),
            FlagsTestExpression flagsTest => ContainsLambda(expression: flagsTest.Subject),
            InsertedTextExpression inserted => inserted.Parts
                                                       .OfType<ExpressionPart>()
                                                       .Any(predicate: part =>
                                                            ContainsLambda(
                                                                expression: part.Expression)),
            StealExpression steal => ContainsLambda(expression: steal.Operand),
            WaitforExpression waitfor => ContainsLambda(expression: waitfor.Operand) ||
                                         waitfor.Timeout != null &&
                                         ContainsLambda(expression: waitfor.Timeout),
            DependentWaitforExpression dependent =>
                ContainsLambda(expression: dependent.Operand) ||
                dependent.Dependencies.Any(predicate: dep =>
                    ContainsLambda(expression: dep.DependencyExpr)) || dependent.Timeout != null &&
                ContainsLambda(expression: dependent.Timeout),
            CarrierPayloadExpression payload => ContainsLambda(expression: payload.Carrier),
            BackIndexExpression backIndex => ContainsLambda(expression: backIndex.Operand),
            WhenExpression whenExpr => whenExpr.Expression != null &&
                                       ContainsLambda(expression: whenExpr.Expression) ||
                                       whenExpr.Clauses.Any(predicate: clause =>
                                           ContainsLambda(statement: clause.Body)),
            _ => false
        };
    }

    private static IEnumerable<CallExpression> FindCalls(Statement statement)
    {
        switch (statement)
        {
            case BlockStatement block:
                foreach (Statement inner in block.Statements)
                foreach (CallExpression call in FindCalls(statement: inner))
                {
                    yield return call;
                }

                yield break;
            case ReturnStatement { Value: { } value }:
                foreach (CallExpression call in FindCalls(expression: value))
                {
                    yield return call;
                }

                yield break;
            case VariantReturnStatement { Value: { } value }:
                foreach (CallExpression call in FindCalls(expression: value))
                {
                    yield return call;
                }

                yield break;
            case DeclarationStatement
            {
                Declaration: VariableDeclaration { Initializer: { } init }
            }:
                foreach (CallExpression call in FindCalls(expression: init))
                {
                    yield return call;
                }

                yield break;
            case ExpressionStatement exprStmt:
                foreach (CallExpression call in FindCalls(expression: exprStmt.Expression))
                {
                    yield return call;
                }

                yield break;
            case IfStatement ifStmt:
                foreach (CallExpression call in FindCalls(expression: ifStmt.Condition))
                {
                    yield return call;
                }

                foreach (CallExpression call in FindCalls(statement: ifStmt.ThenStatement))
                {
                    yield return call;
                }

                if (ifStmt.ElseStatement != null)
                {
                    foreach (CallExpression call in FindCalls(statement: ifStmt.ElseStatement))
                    {
                        yield return call;
                    }
                }

                yield break;
            case WhenStatement whenStmt:
                if (whenStmt.Expression != null)
                {
                    foreach (CallExpression call in FindCalls(expression: whenStmt.Expression))
                    {
                        yield return call;
                    }
                }

                foreach (WhenClause clause in whenStmt.Clauses)
                {
                    foreach (CallExpression call in FindCalls(statement: clause.Body))
                    {
                        yield return call;
                    }
                }

                yield break;
        }
    }

    private static IEnumerable<CallExpression> FindCalls(Expression expression)
    {
        switch (expression)
        {
            case CallExpression call:
                yield return call;
                foreach (CallExpression nested in FindCalls(expression: call.Callee))
                {
                    yield return nested;
                }

                foreach (Expression arg in call.Arguments)
                {
                    foreach (CallExpression nested in FindCalls(expression: arg))
                    {
                        yield return nested;
                    }
                }

                yield break;
            case MemberExpression member:
                foreach (CallExpression nested in FindCalls(expression: member.Object))
                {
                    yield return nested;
                }

                yield break;
            case NamedArgumentExpression named:
                foreach (CallExpression nested in FindCalls(expression: named.Value))
                {
                    yield return nested;
                }

                yield break;
            case BinaryExpression binary:
                foreach (CallExpression nested in FindCalls(expression: binary.Left))
                {
                    yield return nested;
                }

                foreach (CallExpression nested in FindCalls(expression: binary.Right))
                {
                    yield return nested;
                }

                yield break;
            case UnaryExpression unary:
                foreach (CallExpression nested in FindCalls(expression: unary.Operand))
                {
                    yield return nested;
                }

                yield break;
            case ConditionalExpression conditional:
                foreach (CallExpression nested in FindCalls(expression: conditional.Condition))
                {
                    yield return nested;
                }

                foreach (CallExpression nested in
                         FindCalls(expression: conditional.TrueExpression))
                {
                    yield return nested;
                }

                foreach (CallExpression nested in FindCalls(
                             expression: conditional.FalseExpression))
                {
                    yield return nested;
                }

                yield break;
            case CreatorExpression creator:
                foreach ((string _, Expression value) in creator.MemberVariables)
                foreach (CallExpression nested in FindCalls(expression: value))
                {
                    yield return nested;
                }

                yield break;
        }
    }

    /// <summary>
    /// Yields every expression node reachable from <paramref name="statement"/>, including
    /// nodes inside <c>danger</c> blocks, loops, and variant-return wrappers that the
    /// narrower <see cref="FindCalls(Statement)"/> walker does not traverse.
    /// </summary>
    private static IEnumerable<Expression> EnumerateExpressions(Statement statement)
    {
        switch (statement)
        {
            case BlockStatement block:
                foreach (Statement inner in block.Statements)
                foreach (Expression e in EnumerateExpressions(statement: inner))
                {
                    yield return e;
                }

                yield break;
            case DangerStatement danger:
                foreach (Expression e in EnumerateExpressions(statement: danger.Body))
                {
                    yield return e;
                }

                yield break;
            case ReturnStatement { Value: { } returnValue }:
                foreach (Expression e in EnumerateExpressions(expression: returnValue))
                {
                    yield return e;
                }

                yield break;
            case VariantReturnStatement { Value: { } variantValue }:
                foreach (Expression e in EnumerateExpressions(expression: variantValue))
                {
                    yield return e;
                }

                yield break;
            case ThrowStatement throwStmt:
                foreach (Expression e in EnumerateExpressions(expression: throwStmt.Error))
                {
                    yield return e;
                }

                yield break;
            case ExpressionStatement exprStmt:
                foreach (Expression e in EnumerateExpressions(expression: exprStmt.Expression))
                {
                    yield return e;
                }

                yield break;
            case DeclarationStatement
            {
                Declaration: VariableDeclaration { Initializer: { } init }
            }:
                foreach (Expression e in EnumerateExpressions(expression: init))
                {
                    yield return e;
                }

                yield break;
            case IfStatement ifStmt:
                foreach (Expression e in EnumerateExpressions(expression: ifStmt.Condition))
                {
                    yield return e;
                }

                foreach (Expression e in EnumerateExpressions(statement: ifStmt.ThenStatement))
                {
                    yield return e;
                }

                if (ifStmt.ElseStatement != null)
                {
                    foreach (Expression e in EnumerateExpressions(statement: ifStmt.ElseStatement))
                    {
                        yield return e;
                    }
                }

                yield break;
            case WhileStatement whileStmt:
                foreach (Expression e in EnumerateExpressions(expression: whileStmt.Condition))
                {
                    yield return e;
                }

                foreach (Expression e in EnumerateExpressions(statement: whileStmt.Body))
                {
                    yield return e;
                }

                if (whileStmt.ElseBranch != null)
                {
                    foreach (Expression e in EnumerateExpressions(statement: whileStmt.ElseBranch))
                    {
                        yield return e;
                    }
                }

                yield break;
            case EachStatement eachStmt:
                foreach (Expression e in EnumerateExpressions(statement: eachStmt.Body))
                {
                    yield return e;
                }

                if (eachStmt.ElseBranch != null)
                {
                    foreach (Expression e in EnumerateExpressions(statement: eachStmt.ElseBranch))
                    {
                        yield return e;
                    }
                }

                yield break;
            case WhenStatement whenStmt:
                if (whenStmt.Expression != null)
                {
                    foreach (Expression e in EnumerateExpressions(expression: whenStmt.Expression))
                    {
                        yield return e;
                    }
                }

                foreach (WhenClause clause in whenStmt.Clauses)
                foreach (Expression e in EnumerateExpressions(statement: clause.Body))
                {
                    yield return e;
                }

                yield break;
            case UsingStatement usingStmt:
                foreach (Expression e in EnumerateExpressions(statement: usingStmt.Body))
                {
                    yield return e;
                }

                yield break;
            case LoopStatement loopStmt:
                foreach (Expression e in EnumerateExpressions(statement: loopStmt.Body))
                {
                    yield return e;
                }

                yield break;
        }
    }

    /// <summary>
    /// Yields <paramref name="expression"/> itself plus every nested expression node.
    /// </summary>
    private static IEnumerable<Expression> EnumerateExpressions(Expression expression)
    {
        yield return expression;
        switch (expression)
        {
            case CallExpression call:
                foreach (Expression e in EnumerateExpressions(expression: call.Callee))
                {
                    yield return e;
                }

                foreach (Expression arg in call.Arguments)
                foreach (Expression e in EnumerateExpressions(expression: arg))
                {
                    yield return e;
                }

                yield break;
            case CreatorExpression creator:
                foreach ((string _, Expression value) in creator.MemberVariables)
                foreach (Expression e in EnumerateExpressions(expression: value))
                {
                    yield return e;
                }

                yield break;
            case MemberExpression member:
                foreach (Expression e in EnumerateExpressions(expression: member.Object))
                {
                    yield return e;
                }

                yield break;
            case NamedArgumentExpression named:
                foreach (Expression e in EnumerateExpressions(expression: named.Value))
                {
                    yield return e;
                }

                yield break;
            case BinaryExpression binary:
                foreach (Expression e in EnumerateExpressions(expression: binary.Left))
                {
                    yield return e;
                }

                foreach (Expression e in EnumerateExpressions(expression: binary.Right))
                {
                    yield return e;
                }

                yield break;
            case UnaryExpression unary:
                foreach (Expression e in EnumerateExpressions(expression: unary.Operand))
                {
                    yield return e;
                }

                yield break;
            case ConditionalExpression conditional:
                foreach (Expression e in EnumerateExpressions(expression: conditional.Condition))
                {
                    yield return e;
                }

                foreach (Expression e in EnumerateExpressions(
                             expression: conditional.TrueExpression))
                {
                    yield return e;
                }

                foreach (Expression e in EnumerateExpressions(
                             expression: conditional.FalseExpression))
                {
                    yield return e;
                }

                yield break;
        }
    }

    private static bool ContainsGenericPlaceholder(TypeInfo? type)
    {
        if (type == null)
        {
            return false;
        }

        if (type is GenericParameterTypeInfo or ProtocolSelfTypeInfo)
        {
            return true;
        }

        if (type is { IsGenericDefinition: true, TypeArguments: not { Count: > 0 } })
        {
            return true;
        }

        if (type.TypeArguments is { Count: > 0 } &&
            type.TypeArguments.Any(predicate: ContainsGenericPlaceholder))
        {
            return true;
        }

        return type switch
        {
            WrapperTypeInfo wrapper => ContainsGenericPlaceholder(type: wrapper.InnerType),
            TupleTypeInfo tuple => tuple.ElementTypes.Any(predicate: ContainsGenericPlaceholder),
            VariantTypeInfo variant => variant.Members.Any(predicate: member =>
                member.Type != null && ContainsGenericPlaceholder(type: member.Type)),
            _ => false
        };
    }

    private static IEnumerable<IndexExpression> FindIndexExpressions(ISyntaxTreeNode node)
    {
        if (node is IndexExpression index)
        {
            yield return index;
        }

        foreach (ISyntaxTreeNode child in EnumerateChildren(node: node))
        {
            foreach (IndexExpression nested in FindIndexExpressions(node: child))
            {
                yield return nested;
            }
        }
    }

    private static IEnumerable<ISyntaxTreeNode> EnumerateChildren(ISyntaxTreeNode node)
    {
        foreach (PropertyInfo property in node.GetType()
                                              .GetProperties(
                                                   bindingAttr: System.Reflection.BindingFlags
                                                                   .Instance |
                                                                System.Reflection.BindingFlags
                                                                   .Public))
        {
            if (!property.CanRead || property.GetIndexParameters()
                                             .Length != 0 || property.Name ==
                nameof(ISyntaxTreeNode.Location))
            {
                continue;
            }

            object? value = property.GetValue(obj: node);
            switch (value)
            {
                case null:
                    continue;
                case ISyntaxTreeNode child:
                    yield return child;
                    break;
                case IEnumerable enumerable:
                    foreach (object? item in enumerable)
                    {
                        if (item is ISyntaxTreeNode astNode)
                        {
                            yield return astNode;
                        }
                    }

                    break;
            }
        }
    }
}
