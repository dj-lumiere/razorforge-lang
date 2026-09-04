using System;
using System.Collections.Generic;
using System.Linq;
using Compiler.Resolution;
using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Compiler.Synthesis;

using TypeInfo = TypeInfo;

/// <summary>
/// Generates error handling variants for failable (!) routines.
///
/// Generation rules based on throw/absent usage:
/// - Only absent: try_ (returns T?)
/// - Only throw: try_ (returns T?) + check_ (returns Result&lt;T&gt;)
/// - Both: try_ (returns T?) + lookup_ (returns Lookup&lt;T&gt;)
///
/// Phase 1: Keyword Detection - scan for throw/absent in body
/// Phase 2: Variant Generation - determine which variants to create
/// Phase 3: Code Transformation - generate variant routines
/// </summary>
public sealed class ErrorHandlingGenerator
{
    private const string NoneTypeName = "None";

    private readonly TypeRegistry _registry;

    /// <summary>
    /// Initializes a new instance of the <see cref="ErrorHandlingGenerator"/> class.
    /// </summary>
    /// <param name="registry">The type registry for lookups and registration.</param>
    public ErrorHandlingGenerator(TypeRegistry registry)
    {
        _registry = registry;
    }

    /// <summary>
    /// Generates a variant name for an original routine.
    /// Strips the leading '$' from wired routine names so that "emit" -> "try_emit" (not "try_emit").
    /// </summary>
    /// <param name="prefix">The variant prefix (try, check, lookup).</param>
    /// <param name="original">The original routine.</param>
    /// <returns>The variant name.</returns>
    private static string GenerateVariantName(string prefix, RoutineInfo original)
    {
        string baseName = original.Name;
        return $"{prefix}_{baseName}";
    }

    /// <summary>
    /// Analyzes a failable routine and generates appropriate variants.
    /// </summary>
    /// <param name="routine">The routine to analyze.</param>
    /// <param name="body">The routine's body statement.</param>
    /// <returns>The result containing generated variants and any errors.</returns>
    public ErrorHandlingResult GenerateVariants(RoutineInfo routine, Statement body) =>
        GenerateVariants(routine: routine, body: body, pessimistic: false);

    /// <summary>
    /// Generates wrapper variants. When <paramref name="pessimistic"/> is true, the analysis is
    /// forced to <c>HasThrow=true, HasAbsent=true</c> regardless of body contents — used by
    /// pre-registration for failable routines whose body has no direct <c>throw</c>/<c>absent</c>
    /// (failability is propagated from called <c>!</c> routines) so that <c>try_</c>/<c>lookup_</c>
    /// stub variants exist by name for SA resolution. <see cref="ErrorHandlingVariantPass"/>
    /// later refines them after fixpoint propagation.
    /// </summary>
    public ErrorHandlingResult GenerateVariants(RoutineInfo routine, Statement body, bool pessimistic)
    {
        if (!routine.IsFailable)
        {
            return ErrorHandlingResult.Empty;
        }

        // Phase 1: Keyword Detection (+ pessimistic override + propagated failability merge).
        ErrorHandlingAnalysis analysis = BuildFailabilityAnalysis(routine: routine, body: body,
            pessimistic: pessimistic);

        // Validate: ! functions must use throw, absent, or call other failable functions
        if (analysis is { HasThrow: false, HasAbsent: false })
        {
            return new ErrorHandlingResult
            {
                Error = $"Failable function '{routine.Name}!' must use 'throw' or 'absent'",
                HasThrow = false,
                HasAbsent = false
            };
        }

        // Phase 2: Variant Generation
        var variants = BuildVariants(routine: routine, analysis: analysis);

        return new ErrorHandlingResult
        {
            Variants = variants,
            HasThrow = analysis.HasThrow,
            HasAbsent = analysis.HasAbsent,
            ThrownTypes = analysis.ThrownTypes.ToList()
        };
    }

    /// <summary>
    /// Phase 1: builds the throw/absent analysis for a failable routine — scans the body,
    /// applies the <paramref name="pessimistic"/> override, merges propagated failability from
    /// called <c>!</c> routines, and applies the failable-calls fallback.
    /// </summary>
    private static ErrorHandlingAnalysis BuildFailabilityAnalysis(RoutineInfo routine, Statement body,
        bool pessimistic)
    {
        ErrorHandlingAnalysis analysis = AnalyzeBody(body: body);

        if (pessimistic)
        {
            analysis.HasThrow = true;
            analysis.HasAbsent = true;
        }

        // Propagated failability: callees' HasThrow/HasAbsent/ThrowableTypes are merged in
        // here. ErrorHandlingVariantPass runs a fixpoint over FailableCallees beforehand
        // so by the time we land here, routine.HasThrow/HasAbsent already reflect the
        // transitive closure for routines whose failability is purely propagated
        // (e.g. `routine S64_from_text!(t: Text) -> S64 return S64!(from_text: t)`).
        if (routine.HasThrow) analysis.HasThrow = true;
        if (routine.HasAbsent) analysis.HasAbsent = true;
        foreach (TypeInfo t in routine.ThrowableTypes)
        {
            if (!analysis.ThrownTypes.Contains(t)) analysis.ThrownTypes.Add(t);
        }

        // If no direct or propagated throw/absent info but the routine calls failable
        // routines, conservatively assume throw (legacy behavior for arithmetic-overflow
        // crashable calls etc.).
        if (analysis is { HasThrow: false, HasAbsent: false } && routine.HasFailableCalls)
        {
            analysis.HasThrow = true;
        }

        return analysis;
    }

    /// <summary>
    /// Phase 2: builds the list of wrapper variants (try_ always; check_ for throw-only;
    /// lookup_ for throw+absent) for a failable routine from its <paramref name="analysis"/>.
    /// </summary>
    private List<GeneratedVariant> BuildVariants(RoutineInfo routine, ErrorHandlingAnalysis analysis)
    {
        var variants = new List<GeneratedVariant>();

        // try_ variant is always generated
        RoutineInfo tryVariant = GenerateTryVariant(original: routine);
        variants.Add(item: new GeneratedVariant(Kind: ErrorHandlingVariantKind.Try,
            Routine: tryVariant));

        // check_ variant if only throw (no absent)
        if (analysis is { HasThrow: true, HasAbsent: false })
        {
            RoutineInfo checkVariant = GenerateCheckVariant(original: routine);
            variants.Add(item: new GeneratedVariant(Kind: ErrorHandlingVariantKind.Check,
                Routine: checkVariant));
        }

        // lookup_ variant if both throw and absent
        if (analysis is { HasThrow: true, HasAbsent: true })
        {
            RoutineInfo lookupVariant = GenerateLookupVariant(original: routine);
            // Lookup[None] degenerates to check_ (Result[None]) when the return type is None:
            // absent and return are both None so only throw vs no-throw matters.
            // Use Check kind so TransformBody emits Result carriers in the variant body —
            // if Lookup kind is used, the body emits Lookup[None] but the declaration says Result[None].
            ErrorHandlingVariantKind lookupKind =
                routine.ReturnType == null || routine.ReturnType.Name == NoneTypeName
                    ? ErrorHandlingVariantKind.Check
                    : ErrorHandlingVariantKind.Lookup;
            variants.Add(item: new GeneratedVariant(Kind: lookupKind, Routine: lookupVariant));
        }

        return variants;
    }

    /// <summary>
    /// Phase 1: Analyzes the body for throw/absent keywords.
    /// </summary>
    /// <param name="body">The statement body to analyze.</param>
    /// <returns>Analysis result with throw/absent flags.</returns>
    public static ErrorHandlingAnalysis AnalyzeBody(Statement body)
    {
        var analysis = new ErrorHandlingAnalysis();
        AnalyzeStatementRecursive(statement: body, analysis: analysis);
        return analysis;
    }

    /// <summary>
    /// Quick check: returns true if the body contains at least one throw or absent statement.
    /// Used to filter bodies before storing them for variant generation.
    /// </summary>
    public bool BodyHasThrowOrAbsent(Statement body)
    {
        ErrorHandlingAnalysis analysis = AnalyzeBody(body);
        return analysis.HasThrow || analysis.HasAbsent;
    }

    /// <summary>
    /// Recursively analyzes statements for throw/absent keywords.
    /// </summary>
    /// <param name="statement">The statement to analyze.</param>
    /// <param name="analysis">The analysis result to update.</param>
    private static void AnalyzeStatementRecursive(Statement statement, ErrorHandlingAnalysis analysis)
    {
        switch (statement)
        {
            case ThrowStatement { IsFatal: true }:
                // `pierce` is an uncatchable crash — it does not make the routine recoverably
                // failable, so it contributes no throw/variant surface.
                break;

            case ThrowStatement ts:
                analysis.HasThrow = true;
                if (ts.Error?.ResolvedType is { } thrownType)
                    analysis.ThrownTypes.Add(item: thrownType);
                break;

            case AbsentStatement:
                analysis.HasAbsent = true;
                break;

            case BlockStatement block:
                foreach (Statement stmt in block.Statements)
                {
                    AnalyzeStatementRecursive(statement: stmt, analysis: analysis);
                }

                break;

            case IfStatement ifStmt:
                AnalyzeStatementRecursive(statement: ifStmt.ThenStatement, analysis: analysis);
                if (ifStmt.ElseStatement != null)
                {
                    AnalyzeStatementRecursive(statement: ifStmt.ElseStatement, analysis: analysis);
                }

                break;

            case WhileStatement whileStmt:
                AnalyzeStatementRecursive(statement: whileStmt.Body, analysis: analysis);
                break;

            case EachStatement eachStmt:
                AnalyzeStatementRecursive(statement: eachStmt.Body, analysis: analysis);
                break;

            case WhenStatement whenStmt:
                foreach (WhenClause clause in whenStmt.Clauses)
                {
                    AnalyzeStatementRecursive(statement: clause.Body, analysis: analysis);
                }

                break;

            case DangerStatement dangerStmt:
                AnalyzeStatementRecursive(statement: dangerStmt.Body, analysis: analysis);
                break;

            case LoopStatement loopStmt:
                AnalyzeStatementRecursive(statement: loopStmt.Body, analysis: analysis);
                break;
        }
    }

    /// <summary>
    /// Generates the try_ variant (returns Maybe&lt;T&gt;).
    /// throw -> return None
    /// absent -> return None
    /// </summary>
    /// <param name="original">The original routine.</param>
    /// <returns>The try_ variant routine info.</returns>
    /// <summary>
    /// Generates only a try_ variant for a failable routine. Used for bodyless protocol
    /// memberRoutines (e.g. <c>Iterator[T].emit!</c>) so that for-loop desugaring's call to
    /// <c>iter.try_emit()</c> resolves when <c>iter</c> is typed as the bare protocol.
    /// </summary>
    public RoutineInfo GenerateTryVariantStub(RoutineInfo original) =>
        GenerateTryVariant(original: original);

    private RoutineInfo GenerateTryVariant(RoutineInfo original)
    {
        TypeInfo noneType = _registry.LookupType(name: NoneTypeName) ??
            throw new InvalidOperationException(message: "None type not registered");
        TypeInfo returnType = original.ReturnType ?? noneType;

        // try_x on a None-returning routine -> returns Bool (true=success, false=absent/throw)
        // Maybe[None] = { i1, void } is not valid LLVM, so Bool is used directly.
        if (returnType.Name == NoneTypeName)
        {
            TypeInfo boolType = _registry.LookupType(name: "Bool") ??
                throw new InvalidOperationException(message: "Bool type not registered");

            return new RoutineInfo(name: GenerateVariantName(prefix: "try", original: original))
            {
                Kind = original.Kind,
                OwnerType = original.OwnerType,
                MeType = original.MeType,
                Parameters = original.Parameters,
                ReturnType = boolType,
                IsFailable = false,
                IsSynthesized = true,
                DeclaredMutation = original.DeclaredMutation,
                MutationCategory = original.MutationCategory,
                GenericParameters = original.GenericParameters,
                GenericConstraints = original.GenericConstraints,
                Visibility = original.Visibility,
                Location = original.Location,
                Module = original.Module,
                ModulePath = original.ModulePath,
                Annotations = original.Annotations,
                CallingConvention = original.CallingConvention,
                Storage = original.Storage,
                FailableVariant = FailableVariant.TryBool,
                OriginalName = original.Name
            };
        }

        TypeInfo carrierInner = WrapBareEntityForCarrier(type: returnType);

        TypeInfo maybeDef = _registry.LookupType(name: "Maybe") ??
            throw new InvalidOperationException(message: "Maybe type not registered");
        TypeInfo maybeType = _registry.GetOrCreateResolution(
            genericDef: maybeDef,
            typeArguments: [carrierInner]);

        return new
            RoutineInfo(name: GenerateVariantName(prefix: "try", original: original))
            {
                Kind = original.Kind,
                OwnerType = original.OwnerType,
                MeType = original.MeType,
                Parameters = original.Parameters,
                ReturnType = maybeType,
                IsFailable = false, // try_ variants don't fail
                IsSynthesized = true,
                DeclaredMutation = original.DeclaredMutation,
                MutationCategory = original.MutationCategory,
                GenericParameters = original.GenericParameters,
                GenericConstraints = original.GenericConstraints,
                Visibility = original.Visibility,
                Location = original.Location,
                Module = original.Module,
                ModulePath = original.ModulePath,
                Annotations = original.Annotations,
                CallingConvention = original.CallingConvention,
                Storage = original.Storage,
                OriginalName = original.Name
            };
    }

    /// <summary>
    /// Generates the check_ variant (returns Result&lt;T&gt;).
    /// throw -> return error
    /// </summary>
    /// <param name="original">The original routine.</param>
    /// <returns>The check_ variant routine info.</returns>
    private RoutineInfo GenerateCheckVariant(RoutineInfo original)
    {
        // check_ returns Result[T] — success carries T, throw carries the error.
        TypeInfo innerType = original.ReturnType ??
            _registry.LookupType(name: NoneTypeName) ??
            throw new InvalidOperationException(message: "None type not registered");

        TypeInfo carrierInner = WrapBareEntityForCarrier(type: innerType);

        TypeInfo resultDef = _registry.LookupType(name: "Result") ??
            throw new InvalidOperationException(message: "Result type not registered");
        TypeInfo resultType = _registry.GetOrCreateResolution(
            genericDef: resultDef,
            typeArguments: [carrierInner]);

        return new
            RoutineInfo(name: GenerateVariantName(prefix: "check", original: original))
            {
                Kind = original.Kind,
                OwnerType = original.OwnerType,
                MeType = original.MeType,
                Parameters = original.Parameters,
                ReturnType = resultType,
                IsFailable = false, // check_ variants don't fail
                IsSynthesized = true,
                DeclaredMutation = original.DeclaredMutation,
                MutationCategory = original.MutationCategory,
                GenericParameters = original.GenericParameters,
                GenericConstraints = original.GenericConstraints,
                Visibility = original.Visibility,
                Location = original.Location,
                Module = original.Module,
                ModulePath = original.ModulePath,
                Annotations = original.Annotations,
                CallingConvention = original.CallingConvention,
                Storage = original.Storage,
                OriginalName = original.Name
            };
    }

    /// <summary>
    /// Generates the lookup_ variant (returns Lookup&lt;T&gt;).
    /// throw -> return error
    /// absent -> return None
    /// </summary>
    /// <param name="original">The original routine.</param>
    /// <returns>The lookup_ variant routine info.</returns>
    private RoutineInfo GenerateLookupVariant(RoutineInfo original)
    {
        TypeInfo noneType = _registry.LookupType(name: NoneTypeName) ??
            throw new InvalidOperationException(message: "None type not registered");
        TypeInfo returnType = original.ReturnType ?? noneType;

        // Lookup[None] degenerates to Result[None]: absent and return are both None,
        // so the only distinction is throw vs no-throw — same as check_.
        if (returnType.Name == NoneTypeName)
        {
            TypeInfo resultDef = _registry.LookupType(name: "Result") ??
                throw new InvalidOperationException(message: "Result type not registered");
            TypeInfo resultType = _registry.GetOrCreateResolution(
                genericDef: resultDef,
                typeArguments: [noneType]);

            // Degenerated: Lookup[None] -> Result[None], and the API name becomes check_ not lookup_
            return new RoutineInfo(name: GenerateVariantName(prefix: "check", original: original))
            {
                Kind = original.Kind,
                OwnerType = original.OwnerType,
                MeType = original.MeType,
                Parameters = original.Parameters,
                ReturnType = resultType,
                IsFailable = false,
                IsSynthesized = true,
                DeclaredMutation = original.DeclaredMutation,
                MutationCategory = original.MutationCategory,
                GenericParameters = original.GenericParameters,
                GenericConstraints = original.GenericConstraints,
                Visibility = original.Visibility,
                Location = original.Location,
                Module = original.Module,
                ModulePath = original.ModulePath,
                Annotations = original.Annotations,
                CallingConvention = original.CallingConvention,
                Storage = original.Storage,
                OriginalName = original.Name
            };
        }

        TypeInfo carrierInner = WrapBareEntityForCarrier(type: returnType);

        TypeInfo lookupDef = _registry.LookupType(name: "Lookup") ??
            throw new InvalidOperationException(message: "Lookup type not registered");
        TypeInfo lookupType = _registry.GetOrCreateResolution(
            genericDef: lookupDef,
            typeArguments: [carrierInner]);

        return new
            RoutineInfo(name: GenerateVariantName(prefix: "lookup", original: original))
            {
                Kind = original.Kind,
                OwnerType = original.OwnerType,
                MeType = original.MeType,
                Parameters = original.Parameters,
                ReturnType = lookupType,
                IsFailable = false, // lookup_ variants don't fail
                IsSynthesized = true,
                DeclaredMutation = original.DeclaredMutation,
                MutationCategory = original.MutationCategory,
                GenericParameters = original.GenericParameters,
                GenericConstraints = original.GenericConstraints,
                Visibility = original.Visibility,
                Location = original.Location,
                Module = original.Module,
                ModulePath = original.ModulePath,
                Annotations = original.Annotations,
                CallingConvention = original.CallingConvention,
                Storage = original.Storage,
                OriginalName = original.Name
            };
    }

    /// <summary>
    /// Carrier-shape adjustment for failable return types. Post-Owned-retirement,
    /// bare entity <c>T</c> IS the lvalue/bound form, so <c>Maybe[T]</c> /
    /// <c>Result[T]</c> / <c>Lookup[T]</c> over a bare entity is the correct shape:
    /// the carrier owns the bound entity directly, no <c>T</c> intermediary.
    /// Identity for all inputs; retained for the call-site hook in case future
    /// carrier-element transforms (e.g., needs-RecordType relaxation) want a single
    /// chokepoint.
    /// </summary>
    private TypeInfo WrapBareEntityForCarrier(TypeInfo type) => type;
}
