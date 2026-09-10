using Builder.LlvmEmit;
using Builder.Targeting;
using Builder.Verification;
using Builder.Verification.Results;
using SyntaxTree;
using TypeModel.Enums;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Drives the full in-process compile pipeline (parse → semantic analysis + lowering + monomorphization
/// → LLVM IR emission) over feature-rich RazorForge and Suflae programs. Unlike the spawned
/// <c>buildandrun</c> stdlib harness (whose child process is not coverage-instrumented), these run the
/// compiler IN the test host, so they exercise — and count coverage for — the lowering/instantiation/
/// codegen passes that only fire for constructs like pattern matching, lambdas, generics, error-handling
/// variants, iterator inlining, and Suflae module globals.
///
/// Each case asserts the program analyzes without errors and emits non-empty IR; the point is breadth of
/// executed compiler paths, not output shape (the stdlib harness owns behavioral snapshots).
/// </summary>
public class FeatureCoverageTests
{
    /// <summary>Parse + analyze + codegen a RazorForge program to IR, asserting a clean compile.</summary>
    private static string GenerateIr(string source)
    {
        Program program = Parse(source: source);
        var analyzer = new SemanticVerifier(language: Language.RazorForge,
            buildMode: RfBuildMode.ReleaseSpace);
        AnalysisResult result = analyzer.Analyze(program: program);
        AssertNoErrors(result: result);

        string ir = Emit(program: program, result: result);
        Assert.False(condition: string.IsNullOrWhiteSpace(value: ir),
            userMessage: "expected non-empty IR");
        return ir;
    }

    /// <summary>Suflae sibling of <see cref="GenerateIr"/> — analyzed in Suflae realm.</summary>
    private static string GenerateIrSuflae(string source)
    {
        Program program = ParseSuflae(source: source);
        var analyzer = new SemanticVerifier(language: Language.Suflae,
            buildMode: RfBuildMode.ReleaseSpace);
        AnalysisResult result = analyzer.Analyze(program: program);
        AssertNoErrors(result: result);

        string ir = Emit(program: program, result: result);
        Assert.False(condition: string.IsNullOrWhiteSpace(value: ir),
            userMessage: "expected non-empty IR");
        return ir;
    }

    private static string Emit(Program program, AnalysisResult result)
    {
        var generator = new LlvmEmitter(program: program,
            registry: result.Registry,
            options: new LlvmEmitterOptions
            {
                StdlibPrograms = result.Registry.StdlibPrograms,
                BuildMode = RfBuildMode.ReleaseSpace,
                SynthesizedBodies = result.SynthesizedBodies,
                InstantiatedGenericBodies = result.InstantiatedGenericBodies
            });
        return generator.Generate();
    }

    private static void AssertNoErrors(AnalysisResult result)
    {
        if (result.Errors.Count > 0)
        {
            string msgs = string.Join(separator: "\n",
                values: result.Errors.Select(selector: e => $"  - {e.Message} at {e.Location}"));
            Assert.Fail(message: $"Expected no errors but got {result.Errors.Count}:\n{msgs}");
        }
    }

    // ---- pattern matching / when / is-binding ---------------------------------------------------

    [Fact]
    public void When_TypeArmsWithGuardAndBinding_Compiles()
    {
        const string source = """
                              module Test/Feat/When
                              import IO/Console

                              variant Shape
                                S32
                                Text
                                None

                              routine classify(s: Shape) -> Text
                                when s
                                  is S32 n and 0 < n < 100 => return f"small:{n}"
                                  is S32 n                 => return f"big:{n}"
                                  is Text t and t.count() > 3 => return f"long:{t}"
                                  is Text t                => return f"short:{t}"
                                  else                     => return "none"

                              routine start()
                                var a: Shape = 7_s32
                                var b: Shape = "hello"
                                show(classify(s: a))
                                show(classify(s: b))
                                return
                              """;
        _ = GenerateIr(source: source);
    }

    [Fact]
    public void When_LiteralAndRangeArmsOnScalar_Compiles()
    {
        const string source = """
                              module Test/Feat/WhenScalar
                              import IO/Console

                              routine classify(n: S64) -> Text
                                when n
                                  == 0  => return "zero"
                                  < 0   => return "negative"
                                  > 100 => return "huge"
                                  else  => return "positive"

                              routine start()
                                show(classify(n: 0))
                                show(classify(n: -5))
                                show(classify(n: 200))
                                return
                              """;
        _ = GenerateIr(source: source);
    }

    [Fact]
    public void IfIsBinding_ScopedToThenBranch_Compiles()
    {
        const string source = """
                              module Test/Feat/IfIs
                              import IO/Console

                              variant Shape
                                S32
                                Text
                                None

                              routine describe(s: Shape) -> Text
                                if s is S32 n
                                  return f"s32:{n}"
                                if s is Text t
                                  return f"text:{t}"
                                return "none"

                              routine start()
                                var a: Shape = 7_s32
                                show(describe(s: a))
                                return
                              """;
        _ = GenerateIr(source: source);
    }

    [Fact]
    public void When_MaybeBindingElseForm_Compiles()
    {
        const string source = """
                              module Test/Feat/WhenMaybe
                              import IO/Console

                              routine start()
                                var m: Maybe[S64] = 42_s64
                                when m
                                  is None => show("none")
                                  else v  => show(f"bound:{v}")
                                return
                              """;
        _ = GenerateIr(source: source);
    }

    // ---- lambdas / closures ---------------------------------------------------------------------

    [Fact]
    public void Lambda_CaptureViaGiven_Compiles()
    {
        const string source = """
                              module Test/Feat/Lambda
                              import IO/Console

                              routine make_adder(n: S64) -> Routine[(S64,), S64]
                                return x given n => x + n

                              routine start()
                                var add5 = make_adder(n: 5)
                                show(f"{add5(10_s64)}")
                                return
                              """;
        _ = GenerateIr(source: source);
    }

    [Fact]
    public void Lambda_AnnotatedParam_Compiles()
    {
        const string source = """
                              module Test/Feat/LambdaAnnot
                              routine test() -> S32
                                var double_it = (x: S32) => x * 2_s32
                                return double_it(21_s32)
                              """;
        _ = GenerateIr(source: source);
    }

    // ---- iterator inlining / IterTools ----------------------------------------------------------

    [Fact]
    public void IterTools_WhereSelectTakeChain_Compiles()
    {
        const string source = """
                              module Test/Feat/Iter
                              import IO/Console
                              import IterTools

                              routine start()
                                var src: List[S64] = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10]
                                var evens = src.where(predicate: x => x % 2 == 0).List()
                                var doubled = src.select(transform: x => x * 2).List()
                                var first3 = src.take(count: 3_u64).List()
                                show(f"{evens} {doubled} {first3}")
                                return
                              """;
        _ = GenerateIr(source: source);
    }

    [Fact]
    public void EachLoop_OverRangeAndCollection_Compiles()
    {
        const string source = """
                              module Test/Feat/Each
                              import IO/Console

                              routine start()
                                var total = 0_s64
                                each x in 1 to 5
                                  total = total + x
                                var xs: List[S64] = [10, 20, 30]
                                each v in xs
                                  total = total + v
                                show(f"{total}")
                                return
                              """;
        _ = GenerateIr(source: source);
    }

    // ---- generics / monomorphization ------------------------------------------------------------

    [Fact]
    public void GenericRoutine_WithProtocolConstraints_Compiles()
    {
        const string source = """
                              module Test/Feat/Generic
                              import IO/Console

                              routine in_set[T](items: Accessing[List[T]], target: T) -> Bool
                              needs T obeys Equatable, Hashable
                                var s: Set[T] = Set[T]()
                                each x in items
                                  discard s.add(value: x)
                                return target in s

                              routine start()
                                var nums: List[S64] = [10, 20, 30]
                                using nums.view() as g
                                  show(f"{in_set(items: g, target: 20_s64)}")
                                var words: List[Text] = ["a", "b"]
                                using words.view() as g
                                  show(f"{in_set(items: g, target: "b")}")
                                return
                              """;
        _ = GenerateIr(source: source);
    }

    [Fact]
    public void GenericRecord_Instantiated_Compiles()
    {
        const string source = """
                              module Test/Feat/GenericRec
                              import IO/Console

                              record Pair[A, B]
                                first: A
                                second: B

                              routine start()
                                var p = Pair[S64, Text](first: 1_s64, second: "one")
                                show(f"{p.first} {p.second}")
                                return
                              """;
        _ = GenerateIr(source: source);
    }

    // ---- error handling variants ----------------------------------------------------------------

    [Fact]
    public void ErrorHandling_FailableWithCheckVariant_Compiles()
    {
        const string source = """
                              module Test/Feat/Errors
                              import IO/Console

                              routine guard_div!(a: S64, b: S64) -> S64
                                if b == 0
                                  throw DivisionByZeroError()
                                return a // b

                              routine start()
                                var ok = check_guard_div(a: 10, b: 2)
                                var err = check_guard_div(a: 10, b: 0)
                                when ok
                                  is DivisionByZeroError => show("caught")
                                  else v                 => show(f"ok:{v}")
                                when err
                                  is DivisionByZeroError e => show(f"caught:{e.crash_title()}")
                                  else v                   => show(f"ok:{v}")
                                return
                              """;
        _ = GenerateIr(source: source);
    }

    [Fact]
    public void ErrorHandling_TryVariantToMaybe_Compiles()
    {
        const string source = """
                              module Test/Feat/ErrorsTry
                              import IO/Console

                              routine parse_pos!(n: S64) -> S64
                                if n < 0
                                  throw VerificationFailedError()
                                return n

                              routine start()
                                var m = try_parse_pos(n: 5)
                                when m
                                  is None => show("none")
                                  else v  => show(f"{v}")
                                return
                              """;
        _ = GenerateIr(source: source);
    }

    // ---- operators / lowering -------------------------------------------------------------------

    [Fact]
    public void Operators_CheckedWrappingClampingAndShifts_Compile()
    {
        const string source = """
                              module Test/Feat/Ops
                              import IO/Console

                              routine start()
                                var a = 200_u8
                                var wrapped = a +% 100_u8
                                var clamped = a +^ 100_u8
                                var shifted = 1_u32 << 4_u32
                                var logical = 255_u8 >>> 1_u32
                                show(f"{wrapped} {clamped} {shifted} {logical}")
                                return
                              """;
        _ = GenerateIr(source: source);
    }

    [Fact]
    public void Operators_ChainedComparison_Compiles()
    {
        const string source = """
                              module Test/Feat/Chain
                              import IO/Console

                              routine in_range(x: S64) -> Bool
                                return 0 <= x <= 10

                              routine start()
                                show(f"{in_range(x: 5)} {in_range(x: 20)}")
                                return
                              """;
        _ = GenerateIr(source: source);
    }

    // ---- collections ----------------------------------------------------------------------------

    [Fact]
    public void Collections_ListDictSetLiterals_Compile()
    {
        const string source = """
                              module Test/Feat/Collections
                              import IO/Console
                              import Collections

                              routine start()
                                var xs: List[S64] = [1, 2, 3]
                                var d: Dict[Text, S64] = {"a": 1, "b": 2}
                                var s: Set[S64] = {1, 2, 3}
                                show(f"{xs.count()} {d.count()} {s.count()}")
                                return
                              """;
        _ = GenerateIr(source: source);
    }

    // ---- records / derives / wired routines -----------------------------------------------------

    [Fact]
    public void Record_EqHashDerive_Compiles()
    {
        const string source = """
                              module Test/Feat/Derive
                              import IO/Console
                              import Collections

                              record Point obeys Equatable, Hashable
                                x: S64
                                y: S64

                              routine start()
                                var a = Point(x: 1_s64, y: 2_s64)
                                var b = Point(x: 1_s64, y: 2_s64)
                                show(f"{a == b}")
                                var s = Set[Point]()
                                discard s.add(value: a)
                                discard s.add(value: b)
                                show(f"{s.count()}")
                                return
                              """;
        _ = GenerateIr(source: source);
    }

    [Fact]
    public void Variant_RepresentAndDiagnose_Compiles()
    {
        const string source = """
                              module Test/Feat/Variant
                              import IO/Console

                              variant Number
                                S64
                                F64
                                Text
                                None

                              routine describe(n: Number) -> Text
                                when n
                                  is S64 v  => return f"int:{v}"
                                  is F64 v  => return f"float:{v}"
                                  is Text v => return f"text:{v}"
                                  is None   => return "none"

                              routine start()
                                var a: Number = 42_s64
                                show(f"{describe(n: a)}")
                                show(f"{a}")
                                show(f"{a:?}")
                                return
                              """;
        _ = GenerateIr(source: source);
    }

    // ---- entities / ownership -------------------------------------------------------------------

    [Fact]
    public void Entity_MemberRoutineAndSteal_Compiles()
    {
        const string source = """
                              module Test/Feat/Entity
                              import IO/Console

                              entity Counter
                                value: S64

                              routine Counter.bump(amount: S64)
                                me.value = me.value + amount
                                return

                              routine consume(c: Counter)
                                show(f"{c.value}")
                                return

                              routine start()
                                var c = Counter(value: 10)
                                c.bump(amount: 5)
                                consume(c: steal c)
                                return
                              """;
        _ = GenerateIr(source: source);
    }

    // ---- f-strings / format specs ---------------------------------------------------------------

    [Fact]
    public void FString_FormatSpecs_Compile()
    {
        // RazorForge f-text supports exactly four specs: {x} represent, {x:?} diagnose,
        // {x:=} "name=" + represent, {x:=?} "name=" + diagnose.
        const string source = """
                              module Test/Feat/FString
                              import IO/Console

                              routine start()
                                var n: S64 = 255
                                var t: Text = "hi"
                                show(f"{n}")
                                show(f"{n:?}")
                                show(f"{n:=}")
                                show(f"{t:=?}")
                                return
                              """;
        _ = GenerateIr(source: source);
    }

    // ---- control flow ---------------------------------------------------------------------------

    [Fact]
    public void ControlFlow_BreakContinueLoop_Compiles()
    {
        const string source = """
                              module Test/Feat/Loops
                              import IO/Console

                              routine start()
                                each x in 1 to 10
                                  if x == 3
                                    continue
                                  if x > 5
                                    break
                                  show(f"{x}")
                                var n = 0_s64
                                loop
                                  n = n + 1
                                  if n >= 3
                                    break
                                show(f"{n}")
                                return
                              """;
        _ = GenerateIr(source: source);
    }

    [Fact]
    public void Tuple_ReturnAndDestructure_Compiles()
    {
        const string source = """
                              module Test/Feat/Tuple
                              import IO/Console

                              @positional
                              routine divmod_pair(a: S64, b: S64) -> (S64, S64)
                                return (a // b, a % b)

                              routine start()
                                var (q, r) = divmod_pair(17_s64, 5_s64)
                                show(f"{q} {r}")
                                return
                              """;
        _ = GenerateIr(source: source);
    }

    // ---- Suflae realm: module globals + script-ish program --------------------------------------

    [Fact]
    public void Suflae_ModuleGlobal_Compiles()
    {
        const string source = """
                              module Test/Feat/SfGlobal
                              import IO/Console

                              global counter: S64 = 0

                              routine bump()
                                counter = counter + 1
                                return

                              routine start()
                                bump()
                                bump()
                                show(f"{counter}")
                                return
                              """;
        _ = GenerateIrSuflae(source: source);
    }

    [Fact]
    public void Suflae_DependentGlobals_Compile()
    {
        const string source = """
                              module Test/Feat/SfGlobal2
                              import IO/Console

                              global base: S64 = 10
                              global derived: S64 = base + 5

                              routine start()
                                show(f"{base} {derived}")
                                return
                              """;
        _ = GenerateIrSuflae(source: source);
    }

    [Fact]
    public void Suflae_BasicArithmeticProgram_Compiles()
    {
        const string source = """
                              module Test/Feat/SfBasic
                              import IO/Console

                              routine start()
                                var x = 41
                                var y = x + 1
                                show(f"{y}")
                                return
                              """;
        _ = GenerateIrSuflae(source: source);
    }
}
