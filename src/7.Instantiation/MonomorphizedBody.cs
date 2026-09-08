using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Compiler.Instantiation;

/// <summary>
/// A pre-computed monomorphization result produced at synthesis time (Phase 7).
/// Contains everything the code generator needs to emit a concrete generic memberRoutine body
/// without re-doing any AST search or type-substitution building.
/// </summary>
/// <param name="Ast">The AST.</param>
/// <param name="Info">The info.</param>
/// <param name="TypeSubs">The type substitutions (consumed by BuilderQueryInliningPass to fold
///   BuilderQuery constants against the concrete owner; NOT used by codegen — Track C removed the
///   codegen-time substitution map).</param>
/// <param name="VariantStatus">The variant status.</param>
/// <param name="VariantInnerType">The variant inner type.</param>
/// <param name="IsSynthesized">Whether this is synthesized.</param>
public sealed record MonomorphizedBody(
    RoutineDeclaration Ast,

    RoutineInfo Info,

    Dictionary<string, TypeInfo> TypeSubs,

    FailableVariant? VariantStatus,

    TypeInfo? VariantInnerType,

    bool IsSynthesized);
