using Compiler.Instantiation;
using Compiler.Instantiation.Passes;
using Compiler.Declaration;
using SyntaxTree;

namespace Compiler.Verification.Results;

/// <summary>
/// Result of semantic analysis.
/// </summary>
/// <param name="Registry">The populated type registry.</param>
/// <param name="Errors">List of semantic errors.</param>
/// <param name="Warnings">List of semantic warnings.</param>
/// <param name="ParsedLiterals">Parsed literal values for code generation (f128, d32, d64, d128, Integer, Decimal).</param>
/// <param name="SynthesizedBodies">AST bodies for compiler-generated routines (derived operators + variant bodies),
/// keyed by RoutineInfo.RegistryKey. Includes both ne/lt/etc. operators and pre-transformed
/// try_/check_/lookup_ variant bodies produced by <see cref="ErrorHandlingVariantPass"/>.</param>
/// <param name="InstantiatedGenericBodies">Concrete generic memberRoutine bodies produced by
/// <see cref="GenericMonomorphizationPass"/>, keyed by the concrete
/// RoutineInfo.RegistryKey. Codegen uses these to skip AST search and re-rewriting
/// for all generic instantiations visible during semantic analysis.</param>
/// <param name="LiveRoutineKeys">Reachable routine RegistryKeys computed by
/// <see cref="RoutineReachabilityPass"/>. Codegen Phase A uses this to gate stdlib
/// body emission so unreachable routines are not emitted.</param>
/// <param name="LiveOwnerTypeNames">Live concrete owner type full-names from RoutineReachabilityPass.
/// GMP gates monomorphization on membership so unreachable generic instances are skipped.</param>
/// <param name="MaySuspendRoutineKeys">RegistryKeys of routines that can transitively reach a
/// coroutine suspend point (<see cref="MaySuspendAnalysis"/>). Codegen instruments only these with
/// cancellation push/pop (9-2). Empty for any program that never reaches a suspend primitive.</param>
public sealed record AnalysisResult(
    TypeRegistry Registry,
    List<SemanticError> Errors,
    List<SemanticWarning> Warnings,
    IReadOnlyDictionary<SourceLocation, ParsedLiteral> ParsedLiterals,
    IReadOnlyDictionary<string, Statement> SynthesizedBodies,
    IReadOnlyDictionary<string, MonomorphizedBody> InstantiatedGenericBodies,
    IReadOnlyCollection<string> LiveRoutineKeys,
    IReadOnlyCollection<string> LiveOwnerTypeNames,
    IReadOnlyCollection<string> MaySuspendRoutineKeys)
{
    /// <summary>Whether analysis completed without errors.</summary>
    public bool Success => Errors.Count == 0;
}
