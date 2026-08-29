using System;
using System.Collections.Generic;
using System.Linq;
using Compiler.Diagnostics;
using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Verification;

using TypeSymbol = TypeInfo;

#region Numeric Type Classification

/// <summary>
/// Classification of numeric types for type checking purposes.
/// </summary>
internal enum NumericTypeKind
{
    /// <summary>Not a numeric type.</summary>
    None,

    /// <summary>Signed fixed-width integers (s8, s16, s32, s64, s128).</summary>
    SignedInteger,

    /// <summary>Unsigned fixed-width integers (u8, u16, u32, u64, u128).</summary>
    UnsignedInteger,

    /// <summary>Address-sized unsigned integer (Address).</summary>
    Address,

    /// <summary>Binary floating point (f16, f32, f64, f128).</summary>
    BinaryFloat,

    /// <summary>Decimal floating point (d32, d64, d128).</summary>
    DecimalFloat,

    /// <summary>Arbitrary precision integer (Suflae Integer).</summary>
    ArbitraryInteger,

    /// <summary>Arbitrary precision decimal (Suflae Decimal).</summary>
    ArbitraryDecimal,

    /// <summary>Exact rational number (Suflae Fraction).</summary>
    Fraction
}

#endregion

/// <summary>
/// Helper memberRoutines for analysis.
/// </summary>
public sealed partial class SemanticVerifier
{
    private const string MaybeTypeName = "Maybe";

    #region Carrier Type Helpers

    /// <summary>
    /// Returns the base name ("Maybe", "Result", or "Lookup") for a carrier type,
    /// or null if the type is not a carrier type.
    /// Works for both generic definitions (name == "Maybe") and resolved instances (GenericDefinition.Name == "Maybe").
    /// </summary>
    private static string? GetCarrierBaseName(TypeSymbol type)
    {
        if (type is not RecordTypeInfo r)
        {
            return null;
        }

        string baseName = r.GenericDefinition?.Name ?? r.Name;
        return baseName is MaybeTypeName or "Result" or "Lookup" ? baseName : null;
    }

    /// <summary>
    /// Returns true if the type is a carrier type (Maybe, Result, or Lookup).
    /// </summary>
    private static bool IsCarrierType(TypeSymbol type) => GetCarrierBaseName(type: type) != null;

    /// <summary>
    /// Returns true if the type is a Maybe carrier type.
    /// </summary>
    private static bool IsMaybeType(TypeSymbol type) => GetCarrierBaseName(type: type) == MaybeTypeName;

    /// <summary>
    /// Returns true if the type is a legal target for the value-position `none` literal:
    /// Maybe[T] or Lookup[T] (both have an absent arm matched by `is None`), or a variant
    /// that declares a None member. Result[T] is NOT included — Result's two arms are Ok/Err.
    /// </summary>
    private static bool IsNoneCarrierSlot(TypeSymbol type)
    {
        string? carrier = GetCarrierBaseName(type: type);
        if (carrier is MaybeTypeName or "Lookup")
        {
            return true;
        }
        return type is VariantTypeInfo variant && variant.Members.Any(predicate: m => m.IsNone);
    }

    /// <summary>
    /// Checks if a pattern represents a None check.
    /// The parser creates TypePattern(type: "None") rather than NonePattern.
    /// </summary>
    private static bool IsNonePattern(Pattern pattern)
    {
        return pattern is NonePattern or TypePattern { Type.Name: "None" };
    }

    /// <summary>
    /// Checks if a pattern represents a None check.
    /// None is parsed as a regular type pattern.
    /// </summary>
    private static bool IsNoneTypePattern(Pattern pattern)
    {
        return pattern is TypePattern { Type.Name: "None" };
    }

    /// <summary>
    /// Checks if a pattern is the absent arm for a carrier type.
    /// Maybe[T] and Lookup[T] use `is None`. Result[T] has no absent state
    /// (only Crashable | T); when T == None, success matches `is None` in value position.
    /// </summary>
    private static bool IsAbsentPattern(Pattern pattern, TypeSymbol carrierType)
    {
        return GetCarrierBaseName(type: carrierType) switch
        {
            MaybeTypeName or "Lookup" => IsNonePattern(pattern: pattern),
            _ => false
        };
    }

    #endregion

    #region Helper memberRoutines for Analysis

    /// <summary>
    /// Validates argument count and types for a routine call against the routine's parameter list.
    /// Reports errors for too-few arguments, too-many arguments (on non-variadic routines), and type mismatches.
    /// </summary>
    /// <summary>
    /// Named-argument punning (field-init shorthand): when EVERY argument is a bare identifier that
    /// matches a distinct target parameter/field name, rewrite each into <c>name: name</c> in place.
    /// So <c>Point(x, y)</c> == <c>Point(x: x, y: y)</c> — bound by NAME (reorder-safe) and valid even
    /// under the RF-S510 "3+ params need names" rule. All-or-nothing: a partial match is left untouched
    /// so it still follows the normal positional rules instead of becoming a mixed-args S512 error.
    /// </summary>
    private static void PunMatchingNamedArgs(List<Expression> arguments,
        IReadOnlyList<string> targetNames)
    {
        if (arguments.Count == 0) return;
        var names = new HashSet<string>(collection: targetNames, comparer: StringComparer.Ordinal);
        var seen = new HashSet<string>(comparer: StringComparer.Ordinal);
        foreach (Expression arg in arguments)
        {
            if (arg is not IdentifierExpression id || !names.Contains(item: id.Name)
                || !seen.Add(item: id.Name))
                return; // not all bare-identifier-and-distinct-matching → do not pun
        }
        for (int i = 0; i < arguments.Count; i++)
        {
            var id = (IdentifierExpression)arguments[index: i];
            arguments[index: i] =
                new NamedArgumentExpression(Name: id.Name, Value: id, Location: id.Location);
        }
    }

    private void AnalyzeCallArguments(RoutineInfo routine, List<Expression> arguments,
        SourceLocation location, TypeSymbol? callObjectType = null)
    {
        List<ParameterInfo> parameters = routine.Parameters;
        int totalParams = parameters.Count;

        // Field-init shorthand: pun bare identifiers matching parameter names into named args.
        PunMatchingNamedArgs(arguments: arguments,
            targetNames: parameters.Where(predicate: p => p.Name != "me")
                                   .Select(selector: p => p.Name).ToList());

        // Step 1: Validate named argument ordering and build parameter bindings.
        // Each entry maps parameter index -> argument expression.
        bool seenNamed = false;
        var boundParams = new Dictionary<int, Expression>();
        int positionalIndex = 0;

        // S510: Routines with 3+ non-me parameters require all arguments to be named.
        // This prevents argument-swap bugs at call sites. Variadic routines are exempt
        // because their extra positional args don't map to named parameters.
        // For exactly 2 parameters, naming is recommended (warning W258) — binary ops
        // like swap(a, b) or move(from, to) are usually clear from context.
        int nonMeParamCount =
            parameters.Count(predicate: p => p.Name != "me" && !p.HasDefaultValue);
        bool requiresNamedArgs = nonMeParamCount >= 3 && !routine.IsVariadic;
        bool recommendsNamedArgs = nonMeParamCount == 2 && !routine.IsVariadic;

        // @positional: the routine opts into positional calls. Named arguments always remain
        // legal, so this only RELAXES the S510/W258 rules — it never forbids names.
        // Foreign (C::/LLVM::) routines are positional by nature: their parameter identity is the
        // ARGUMENT ORDER (+ types), not names — a C header carries no binding parameter names, and
        // extern re-declarations may name them differently. So they are implicitly @positional.
        bool isPositional = routine.Annotations.Contains(value: "positional") || routine.IsForeign;
        if (isPositional)
        {
            requiresNamedArgs = false;
            recommendsNamedArgs = false;
        }

        // All-or-nothing (S512): a single call must be EITHER all named OR all positional —
        // mixing the two is always a compile error, for @positional and plain routines alike,
        // because partially-named calls are the most argument-swap-prone shape. The all-positional
        // case is then governed by the count rules above (0/1 ok, 2 warns W258, 3+ requires names
        // S510); the all-named case is always fine. When mixed, S512 subsumes the per-argument
        // W258/S510/S507 diagnostics below (gated on !isMixed) so the call reports once, cleanly.
        bool hasNamedArg = arguments.Any(predicate: a => a is NamedArgumentExpression);
        bool hasPositionalArg =
            arguments.Any(predicate: a => a is not NamedArgumentExpression);
        bool isMixed = hasNamedArg && hasPositionalArg;
        if (isMixed)
        {
            ReportError(code: SemanticDiagnosticCode.MixedPositionalAndNamedArguments,
                message:
                $"Call to '{routine.Name}' mixes positional and named arguments — a call must be " +
                "either all positional or all named.",
                location: location);
        }

        foreach (Expression arg in arguments)
        {
            if (arg is NamedArgumentExpression named)
            {
                seenNamed = true;

                // Look up parameter by name
                int paramIndex = -1;
                for (int j = 0; j < totalParams; j++)
                {
                    if (parameters[index: j].Name == named.Name)
                    {
                        paramIndex = j;
                        break;
                    }
                }

                if (paramIndex == -1)
                {
                    // S505: Unknown named argument
                    ReportError(code: SemanticDiagnosticCode.UnknownNamedArgument,
                        message: $"'{routine.Name}' has no parameter named '{named.Name}'.",
                        location: named.Location);
                    AnalyzeExpression(expression: named.Value);
                }
                else if (boundParams.ContainsKey(key: paramIndex))
                {
                    // S506: Duplicate named argument (parameter already bound)
                    ReportError(code: SemanticDiagnosticCode.DuplicateNamedArgument,
                        message: $"Parameter '{named.Name}' of '{routine.Name}' is already bound.",
                        location: named.Location);
                    AnalyzeExpression(expression: named.Value);
                }
                else
                {
                    boundParams[key: paramIndex] = named.Value;
                }
            }
            else
            {
                if (requiresNamedArgs && !isMixed)
                {
                    // S510: Named argument enforcement — subsumes S507
                    ReportError(code: SemanticDiagnosticCode.NamedArgumentRequired,
                        message:
                        $"Routine '{routine.Name}' has {nonMeParamCount} parameters - all arguments must be named.",
                        location: arg.Location);
                }
                else if (recommendsNamedArgs && !isMixed)
                {
                    // W258: Named arguments recommended for 2-parameter calls.
                    ReportWarning(code: SemanticWarningCode.NamedArgumentRecommended,
                        message:
                        $"Routine '{routine.Name}' has 2 parameters - naming arguments is recommended for clarity.",
                        location: arg.Location);
                }
                else if (seenNamed && !isPositional && !isMixed)
                {
                    // S507: Positional argument after named argument. Mixed calls are already
                    // reported once as S512 above, so this is suppressed for them.
                    ReportError(code: SemanticDiagnosticCode.PositionalAfterNamed,
                        message:
                        $"Positional argument cannot appear after named arguments in call to '{routine.Name}'.",
                        location: arg.Location);
                }

                // For variadic routines: once we reach the varargs parameter,
                // all subsequent positional args are varargs (don't advance past it).
                // Trailing params (sep, end) are only filled via named args or defaults.
                bool inVariadicSlot = routine.IsVariadic && positionalIndex > 0 &&
                                      positionalIndex - 1 < totalParams &&
                                      parameters[index: positionalIndex - 1].IsVariadicParam;

                if (inVariadicSlot)
                {
                    // Variadic extra argument — just analyze it
                    AnalyzeExpression(expression: arg);
                }
                else if (positionalIndex < totalParams)
                {
                    if (boundParams.ContainsKey(key: positionalIndex))
                    {
                        // S506: Positional arg collides with earlier named arg that bound this slot
                        ReportError(code: SemanticDiagnosticCode.DuplicateNamedArgument,
                            message:
                            $"Parameter '{parameters[index: positionalIndex].Name}' of '{routine.Name}' is already bound.",
                            location: arg.Location);
                    }
                    else
                    {
                        boundParams[key: positionalIndex] = arg;
                    }
                }
                else if (!routine.IsVariadic)
                {
                    // Extra positional arg beyond parameter count — handled by count check below
                    boundParams[key: positionalIndex] = arg;
                }
                else
                {
                    // Variadic extra argument — just analyze it
                    AnalyzeExpression(expression: arg);
                }

                if (!inVariadicSlot)
                {
                    positionalIndex++;
                }
            }
        }

        // Step 2: Check argument count against required parameters.
        int requiredParams = parameters.Count(predicate: p => !p.HasDefaultValue);
        int unboundRequired = 0;
        for (int i = 0; i < totalParams; i++)
        {
            if (!boundParams.ContainsKey(key: i) && !parameters[index: i].HasDefaultValue)
            {
                unboundRequired++;
            }
        }

        if (unboundRequired > 0)
        {
            if (requiredParams == totalParams)
            {
                ReportError(code: SemanticDiagnosticCode.TooFewArguments,
                    message:
                    $"'{routine.Name}' expects {totalParams} argument(s), but got {arguments.Count}.",
                    location: location);
            }
            else
            {
                ReportError(code: SemanticDiagnosticCode.TooFewArguments,
                    message:
                    $"'{routine.Name}' expects at least {requiredParams} argument(s), but got {arguments.Count}.",
                    location: location);
            }
        }
        else if (positionalIndex > totalParams && !routine.IsVariadic)
        {
            ReportError(code: SemanticDiagnosticCode.TooManyArguments,
                message:
                $"'{routine.Name}' expects at most {totalParams} argument(s), but got {arguments.Count}.",
                location: location);
        }

        // Step 3: Type-check each bound argument against its parameter.
        foreach (KeyValuePair<int, Expression> binding in boundParams)
        {
            if (binding.Key >= totalParams)
            {
                // Extra positional beyond params (already reported as TooManyArguments)
                AnalyzeExpression(expression: binding.Value);
                continue;
            }

            ParameterInfo param = parameters[index: binding.Key];
            TypeSymbol paramType = param.Type;

            // Variadic params are desugared to Array[T, __VarargN] and the call site packs its trailing
            // args into a single Array[T, K] literal, so the argument is matched against the Array
            // parameter directly — no element unwrapping.

            if (callObjectType != null)
            {
                if (routine.OwnerType is GenericParameterTypeInfo genParamOwner)
                {
                    var substitutions = new Dictionary<string, TypeSymbol>
                    {
                        [key: genParamOwner.Name] = callObjectType
                    };
                    paramType = SubstituteWithMapping(type: paramType,
                        substitutions: substitutions);
                }
                else if (routine.OwnerType is { IsGenericDefinition: true })
                {
                    // Owner like `List[T]` (gen-def) against receiver `List[S64]` — substitute
                    // T → S64 so callback parameter types (e.g. `Routine[(T, T), Bool]`) target-type
                    // lambda parameters correctly. Skip when OwnerType is already a resolution
                    // (e.g. `Hijacked[BTreeListNode[T]]`) — its Parameters are already substituted,
                    // applying again would double-wrap (`T → BTreeListNode[T]` applied to a
                    // `BTreeListNode[T]` param produces `BTreeListNode[BTreeListNode[T]]`).
                    paramType =
                        SubstituteOwnerGenerics(paramType: paramType,
                            lookupType: callObjectType,
                            ownerType: routine.OwnerType) ?? paramType;
                }
            }

            Expression argExpr = binding.Value;
            TypeSymbol argType = AnalyzeExpression(expression: argExpr, expectedType: paramType);

            // Suflae: a NON-NULL entity parameter rejects a possibly-none argument. Every SF entity
            // parameter is non-null — a nullable one would be `Maybe[E]`, a distinct type — so any
            // entity-reference param must be fed a checked (non-none) value. A nullable Roamed arg is
            // still IsAssignableTo the bare-entity param (the E↔Roamed bridge), so this is the only gate.
            if (IsEntityRefType(type: paramType) && IsNullableEntityRead(expr: argExpr))
            {
                ReportNullableIntoNonNull(target: $"parameter '{param.Name}' of '{routine.Name}'",
                    value: argExpr, optionalHint: $"{param.Name}: <Type>?");
            }

            if (argType.Category == TypeCategory.Error || paramType.Category == TypeCategory.Error)
            {
                continue;
            }

            if (!IsAssignableTo(source: argType, target: paramType))
            {
                // Routine -> CPtr coercion (FFI): a NON-CAPTURING routine reference is a C function
                // pointer at the ABI level, so it satisfies a `CPtr` parameter (codegen emits the
                // routine's bare symbol). Restricted to a bare top-level routine NAME — that cannot
                // capture an environment, which a C `void*` could not carry anyway. Routine-typed
                // locals and lambdas are excluded (they may be closures).
                if (IsBareRoutineRefToCPtr(argExpr: argExpr, argType: argType, paramType: paramType))
                {
                    // accept — handled by codegen as a bare function-pointer reference.
                }
                // Skip mismatch when paramType still references an unresolved memberRoutine-level generic
                // (e.g. `Routine[(S64,), U]` for `select[U]`). The caller runs
                // InferMemberRoutineGenericTypeArguments after AnalyzeCallArguments and substitutes the
                // memberRoutine; without this guard we'd report a spurious error before inference resolves U.
                else if (!ContainsUnresolvedMemberRoutineGeneric(type: paramType,
                        genericParameters: routine.GenericParameters))
                {
                    ReportError(code: SemanticDiagnosticCode.ArgumentTypeMismatch,
                        message:
                        $"Argument '{param.Name}' of '{routine.Name}': cannot convert '{argType.Name}' to '{paramType.Name}'.",
                        location: argExpr.Location);
                }
            }

            // C-boundary callback: a routine handed to a C:: / LLVM:: extern becomes a raw C function
            // pointer, which carries NO environment, so only a CAPTURELESS routine can cross (its fat
            // value's `bound` is null → the `fn` word IS a 1-word C fnptr). A capturing routine's `bound`
            // has no slot in the C signature. Capturing-ness is a VALUE property (the fat `{fn, bound}`),
            // NOT a type property — `Routine[T]` deliberately erases it (value-arity == type-arity) — so
            // for a Routine-typed VALUE (a field / local / arbitrary expression) it is only knowable at
            // RUNTIME. Codegen therefore emits a runtime `bound == null` guard at the C boundary for such
            // values (captureless → pass `fn`; capturing → crash). Only a CAPTURING LAMBDA LITERAL is a
            // STATICALLY-certain violation (its `given` clause is right here), so THAT is the sole build-
            // time error; everything else defers to the runtime guard. Fires for a `Routine[...]`/`CPtr`
            // foreign param. A future build-time analysis (RF has no nested routines, so a value can be
            // traced to its source routine) could recover more cases statically.
            bool isCapturingLambdaLiteral = argExpr is LambdaExpression { Captures.Count: > 0 };
            if (routine.IsForeign
                && argType is RoutineTypeInfo
                && (paramType is RoutineTypeInfo || paramType.Name == "CPtr")
                && isCapturingLambdaLiteral)
            {
                ReportError(code: SemanticDiagnosticCode.ForeignCallbackMustBeNonCapturing,
                    message:
                    $"Argument '{param.Name}' of C routine '{routine.Name}' is a CAPTURING lambda, which cannot " +
                    "cross the C boundary — a routine handed to C becomes a raw function pointer with no " +
                    "environment, and a capture is an extra bound argument with no C slot. Pass a captureless " +
                    "callback and thread any state through an explicit userdata parameter.",
                    location: argExpr.Location);
            }
            else
            {
                // Implicit refer/control coercion for marker-protocol params.
                // Wraps the argument expression as `arg.refer()` / `arg.control()` so
                // codegen, reachability, and call-classification all see a fully resolved
                // routine reference. The wrapper's refer/control memberRoutine returns T (the
                // inner entity), which matches the rewritten signature post-Phase 8.
                TryInjectMarkerCoercion(routine, arguments, binding.Key, paramType, argType);
            }

            // Phase 1: warn when a borrowed reference is passed where the parameter type is not
            // trivially copyable. Mirrors the var-decl / assignment rule — the same explicit
            // verb (steal / .retain() / .track()) must appear at the call site.
            Expression argValue = argExpr is NamedArgumentExpression namedArg
                ? namedArg.Value
                : argExpr;
            // Borrow protocols (Accessing[T] / Controlling[T]) accept the source by reference —
            // no copy/move is happening at the call site, so no verb is required.
            string paramBase = paramType.BareName;
            bool paramIsBorrow = paramType.Category == TypeCategory.Protocol &&
                                 paramBase is Compiler.Resolution.RuntimeContract.Accessing or Compiler.Resolution.RuntimeContract.Controlling;
            if (_registry.Language == Language.RazorForge &&
                argValue is IdentifierExpression or MemberExpression &&
                !IsTriviallyAssignable(type: argType) &&
                !paramIsBorrow)
            {
                var hint = FindNonTriviallyAssignableWrapper(type: argType);
                if (hint != null)
                {
                    string verb = NonTriviallyAssignableWrappers[key: hint.Value.Wrapper];
                    string fieldNote = hint.Value.Path == "<value>"
                        ? $"argument of type '{argType.Name}' is a '{hint.Value.Wrapper}[…]' wrapper"
                        : $"field '{hint.Value.Path}' of type '{hint.Value.Wrapper}[…]'";
                    ReportError(code: SemanticDiagnosticCode.ImplicitWrapperCopy,
                        message:
                        $"Implicit copy in call to '{routine.Name}': {fieldNote} requires an explicit copy verb. " +
                        $"Spell out '{verb}' at the call site, or reconstruct the record with each field's verb.",
                        location: argExpr.Location);
                }
            }

            // Bare entity passed to a CONSUMING parameter needs an explicit `steal` (RF-S413).
            // The old check false-positived because it looked at a stripped type; the reliable
            // signal is STRUCTURAL and read here at Phase 4, BEFORE MarkerProtocolDesugarPass strips
            // borrow params to their inner `T`: a consuming param is bare `EntityTypeInfo`, while
            // every borrow is a Protocol (`Accessing`/`Controlling`) or a Record wrapper
            // (`Viewing`/`Modifying`/…) — never bare `EntityTypeInfo`. So gating on
            // `paramType is EntityTypeInfo` excludes all borrow forms with no name list. Verb-wrapped
            // arguments (`steal x`, `x.copy()`, `x.share()`) are Steal/Call expressions, not
            // Identifier/Member, so they are excluded automatically. Safety comes from move tracking;
            // this check makes the destructive transfer visible in source.
            if (_registry.Language == Language.RazorForge
                && argValue is IdentifierExpression or MemberExpression
                && argType is EntityTypeInfo
                && paramType is EntityTypeInfo)
            {
                ReportError(code: SemanticDiagnosticCode.BareEntityAssignment,
                    message:
                    $"Cannot pass entity '{argType.Name}' to consuming parameter '{param.Name}' of " +
                    $"'{routine.Name}' directly. Use 'steal' for ownership transfer, or pass a borrow.",
                    location: argValue.Location);
            }
        }
    }

    /// <summary>
    /// Re-types lambda arguments after a memberRoutine-generic routine has been resolved. The initial
    /// <see cref="AnalyzeCallArguments"/> pass runs before memberRoutine-level generics are inferred, so a
    /// lambda parameter bound to a memberRoutine generic keeps it unresolved — e.g. `acc` in
    /// <c>accumulate[U](combiner: Routine[(U,T),U])</c> stays <c>U</c> rather than the inferred
    /// <c>S64</c>. Re-analyzing the lambda against the resolved parameter type rewrites
    /// <c>lambda.ResolvedType</c> with concrete parameter types so the post-processing lambda-lift
    /// mangles a defined symbol instead of one carrying an unbound generic.
    /// </summary>
    private void ReanalyzeLambdaArguments(RoutineInfo resolvedMemberRoutine, List<Expression> arguments,
        TypeSymbol? callObjectType)
    {
        IReadOnlyList<ParameterInfo> parameters = resolvedMemberRoutine.Parameters;
        foreach (Expression argExpr in arguments)
        {
            Expression inner = argExpr is NamedArgumentExpression nae ? nae.Value : argExpr;
            if (inner is not LambdaExpression) continue;

            // Named arguments match by parameter name; a bare positional lambda matches the (single)
            // Routine-typed parameter — robust against the implicit `me` receiver offset.
            ParameterInfo? param = argExpr is NamedArgumentExpression named
                ? parameters.FirstOrDefault(predicate: p => p.Name == named.Name)
                : parameters.FirstOrDefault(predicate: p => p.Type is RoutineTypeInfo);
            if (param?.Type is not RoutineTypeInfo) continue;

            TypeSymbol paramType = param.Type;
            // Owner-level generics (T) are substituted the same way AnalyzeCallArguments does, so a
            // `Routine[(U,T),U]` parameter is fully concrete once both U (memberRoutine) and T (owner) bind.
            if (callObjectType != null && resolvedMemberRoutine.OwnerType is { IsGenericDefinition: true })
                paramType = SubstituteOwnerGenerics(paramType: paramType,
                    lookupType: callObjectType,
                    ownerType: resolvedMemberRoutine.OwnerType) ?? paramType;

            AnalyzeExpression(expression: inner, expectedType: paramType);
        }
    }

    /// <summary>
    /// Rewrites `show(x)` / `alert(x)` arguments in-place when `x` is a copy-restricted
    /// wrapper (Owned, Retained, Tracked, ...). Each such argument becomes `x.represent()`
    /// (for show) or `x.diagnose()` (for alert). The display protocols guarantee `@readonly`,
    /// so the memberRoutine call is a borrow — `x` is not consumed. The resulting `Text` matches
    /// the `show(value: Accessing[Text])` / `alert(value: Accessing[Text])` overload
    /// (value-record, no copy verb), so subsequent overload resolution picks that branch
    /// instead of the generic `show[T]` / `alert[T]` that would trigger S420.
    /// </summary>
    /// <remarks>
    /// Must run BEFORE overload resolution — rewriting after a routine has been bound to
    /// `show[T=Owned[...]]` leaves the call with a Text arg but mismatched callee, producing
    /// garbled output at runtime (the wrong function is called).
    ///
    /// Narrow scope (phase 1): only `show` and `alert` are eligible. Other `@readonly`
    /// routines are not rewritten — they don't have a canonical readonly accessor.
    /// </remarks>
    private void RewriteDisplayRoutineWrapperArgs(string callName,
        List<Expression> arguments)
    {
        bool isShow = callName == "show";
        bool isAlert = callName == "alert";
        if (!isShow && !isAlert) return;

        for (int i = 0; i < arguments.Count; i++)
        {
            Expression slot = arguments[index: i];
            Expression innerExpr = slot is NamedArgumentExpression named
                ? named.Value
                : slot;

            // Type-probe before overload resolution. AnalyzeExpression is idempotent for
            // most expression kinds (the result is cached on `.ResolvedType`). Literals
            // re-analyze cheaply.
            TypeSymbol argType = AnalyzeExpression(expression: innerExpr);
            if (argType.Category == TypeCategory.Error) continue;

            // Rewrite for args that don't match the bare-Text/Bytes overloads:
            //   - copy-restricted wrappers (Owned, Retained, Tracked, …) — `IsTriviallyAssignable`
            //     returns false; we need the rewrite to avoid S420.
            //   - raw entities (List[T], Set[T], Dict[K,V]) — `IsTriviallyAssignable` returns
            //     true (fallback), but the generic `alert[T]` / `show[T]` monomorphization
            //     copies the entity ptr by value, which corrupts. Rewriting to `arg.diagnose()`
            //     extracts a Text and uses the cleaner `Accessing[Text]` overload instead.
            bool isEntity = argType is EntityTypeInfo;
            if (!isEntity && IsTriviallyAssignable(type: argType)) continue;

            string memberRoutineName = isAlert
                ? Compiler.Resolution.RuntimeContract.Display.Diagnose
                : Compiler.Resolution.RuntimeContract.Display.Represent;
            var memberAccess = new MemberExpression(
                Object: innerExpr,
                MemberName: memberRoutineName,
                Location: innerExpr.Location);
            var displayCall = new CallExpression(
                Callee: memberAccess,
                Arguments: [],
                Location: innerExpr.Location);
            // ResolvedType is left null — overload resolution will analyze the new
            // CallExpression and pick the Text-typed alert/show overload accordingly.

            arguments[index: i] = slot is NamedArgumentExpression na
                ? na with { Value = displayCall }
                : displayCall;
        }
    }

    /// <summary>
    /// Returns true if the expression can appear on the left-hand side of an assignment.
    /// Valid assignment targets are identifiers, member accesses, and index expressions.
    /// </summary>
    private static bool IsAssignableTarget(Expression target)
    {
        // SpliceMemberExpression (`obj.${m.name}`) becomes a real member access at expansion, so it is
        // an assignable target too — needed for decl-position expand column writes (`result.${m.name} = …`).
        return target is IdentifierExpression or MemberExpression or IndexExpression
            or SpliceMemberExpression;
    }

    /// <summary>
    /// True when <paramref name="argExpr"/> is a bare top-level routine NAME being passed where a
    /// <c>CPtr</c> is expected — the FFI routine-pointer → C-function-pointer coercion. A bare
    /// routine reference cannot capture, so its native symbol is a valid C function pointer; codegen
    /// emits <c>ptr @&lt;routine&gt;</c>. Excludes routine-typed locals and lambdas (possible closures).
    /// </summary>
    private bool IsBareRoutineRefToCPtr(Expression argExpr, TypeSymbol argType, TypeSymbol paramType)
    {
        return paramType.Name == "CPtr"
               && argType is RoutineTypeInfo
               && argExpr is IdentifierExpression id
               && _registry.LookupRoutineByName(name: id.Name) != null;
    }

    /// <summary>True when <paramref name="type"/> is <c>Roamed[E]</c> (record or wrapper form) for the given
    /// entity — used to treat a bare SF entity and its Roamed handle as mutually assignable.</summary>
    private static bool IsRoamedOfEntity(TypeSymbol type, EntityTypeInfo entity)
    {
        string baseName = type switch
        {
            RecordTypeInfo { GenericDefinition: { } gd } => gd.Name,
            WrapperTypeInfo w => w.Name,
            _ => string.Empty
        };
        return baseName == Compiler.Resolution.RuntimeContract.Roamed
            && type.TypeArguments is [{ } inner]
            && inner.FullName == entity.FullName;
    }

    /// <summary>
    /// Returns true if a value of type <paramref name="source"/> can be assigned to a variable of type <paramref name="target"/>.
    /// Handles error types (to suppress cascading errors), generic resolution matching, and protocol conformance.
    /// No implicit numeric or widening conversions are performed.
    /// </summary>
    private bool IsAssignableTo(TypeSymbol source, TypeSymbol target)
    {
        // Same type
        if (source.Name == target.Name)
        {
            return true;
        }

        // FullName equality: handles wrapper types where Name may be bare ("Hijacked") on
        // one side but parameterized ("Hijacked[Core.U8]") on the other due to differing
        // construction paths. FullName normalizes both forms.
        if (source.FullName == target.FullName)
        {
            return true;
        }

        // Suflae: a bare entity `E` and `Roamed[E]` are the same thing to an SF user (entity FIELDS are
        // the `Roamed[E]` record at collection; entity LOCALS stay bare `E` in SA until the Phase-7 SF
        // lowering pass retypes them). Treat them as mutually assignable — the pass + codegen insert the
        // `.roam()` / field release-old+retain-new. RazorForge keeps the strict distinction.
        if (_registry.Language == Language.Suflae)
        {
            // Both `x: E` (non-null) and `x: E?` (optional) fields are `Roamed[E]`, so a bare entity is
            // assignable to either (the pass inserts the roam). `E?`'s `none` is a null Roamed handle.
            if (source is EntityTypeInfo se && IsRoamedOfEntity(type: target, entity: se)) return true;
            if (target is EntityTypeInfo te && IsRoamedOfEntity(type: source, entity: te)) return true;
        }

        // Error types are assignable to anything (to reduce cascading errors)
        if (source.Category == TypeCategory.Error || target.Category == TypeCategory.Error)
        {
            return true;
        }

        // Function-type (lambda) compatibility with return-type COVARIANCE: a lambda returning a
        // concrete type is assignable to a parameter expecting a supertype of that return — e.g.
        // `select_many[S64](transform: x => (1 to x).List())` produces `Routine[(S64,), List[S64]]`
        // for a `Routine[(S64,), Iterable[S64]]` parameter, and `List[S64]` obeys `Iterable[S64]`.
        // Parameter types must line up (the lambda was analyzed against the expected param types, so
        // they already match — accept either assignment direction for robustness).
        if (source is RoutineTypeInfo srcRoutine && target is RoutineTypeInfo tgtRoutine)
        {
            if (srcRoutine.ParameterTypes.Count != tgtRoutine.ParameterTypes.Count)
                return false;
            for (int i = 0; i < srcRoutine.ParameterTypes.Count; i++)
            {
                if (!IsAssignableTo(source: srcRoutine.ParameterTypes[index: i],
                        target: tgtRoutine.ParameterTypes[index: i])
                    && !IsAssignableTo(source: tgtRoutine.ParameterTypes[index: i],
                        target: srcRoutine.ParameterTypes[index: i]))
                    return false;
            }
            if (srcRoutine.ReturnType == null || tgtRoutine.ReturnType == null)
                return srcRoutine.ReturnType == null && tgtRoutine.ReturnType == null;
            return IsAssignableTo(source: srcRoutine.ReturnType, target: tgtRoutine.ReturnType);
        }

        // Variant auto-wrap: any value whose type matches a variant member type is
        // implicitly coerced to the variant (the tag is set automatically). This is
        // how variants are constructed — there is no explicit `Variant.of(...)` form.
        if (target is VariantTypeInfo variantTarget)
        {
            foreach (VariantMemberInfo member in variantTarget.Members)
            {
                if (member.Type != null &&
                    (member.Type.Name == source.Name ||
                     member.Type.FullName == source.FullName))
                {
                    return true;
                }
            }
        }

        // Generic type matching - check if resolution matches definition
        if (target.IsGenericDefinition && source.IsGenericResolution)
        {
            string baseName = source.BareName;
            if (baseName == target.Name)
            {
                return true;
            }
        }

        // Reverse: generic definition assignable to its parameterized form within generic context.
        // e.g., 'me' has type 'Total' (generic def) but return expects 'Total[T]'.
        // Only allowed when all type args are unresolved generic parameters (not concrete types).
        if (source.IsGenericDefinition && target is { IsGenericResolution: true, TypeArguments: not null } &&
            target.TypeArguments.All(predicate: t => t is GenericParameterTypeInfo))
        {
            string baseName = target.BareName;
            if (baseName == source.Name)
            {
                return true;
            }
        }

        // Protocol conformance - if target is a protocol, check if source implements it
        if (target.Category == TypeCategory.Protocol)
        {
            // Borrow protocols (Accessing[T] / Controlling[T]) accept an ownership-carrying or
            // bare source whose inner type matches T. Retained/Modifying are accepted by
            // both; Viewing is readonly so accepted only by Accessing; Hijacked needs explicit
            // .as_entity() — never accepted by implicit borrow coercion.
            string targetBase = target.BareName;
            if ((targetBase == Compiler.Resolution.RuntimeContract.Accessing || targetBase == Compiler.Resolution.RuntimeContract.Controlling) &&
                target.TypeArguments is { Count: 1 } borrowArgs)
            {
                TypeSymbol borrowInner = borrowArgs[index: 0];
                if (TryGetOwnershipWrapperInner(type: source, wrapperBase: out string? srcWrapper,
                        inner: out TypeSymbol? srcInner))
                {
                    bool wrapperAllowed = targetBase == Compiler.Resolution.RuntimeContract.Accessing
                        ? srcWrapper is Compiler.Resolution.RuntimeContract.Retained or Compiler.Resolution.RuntimeContract.Modifying or Compiler.Resolution.RuntimeContract.Viewing
                            or Compiler.Resolution.RuntimeContract.Controlling or Compiler.Resolution.RuntimeContract.Accessing
                        : srcWrapper is Compiler.Resolution.RuntimeContract.Retained or Compiler.Resolution.RuntimeContract.Modifying or Compiler.Resolution.RuntimeContract.Controlling;
                    if (wrapperAllowed && srcInner != null &&
                        (srcInner.FullName == borrowInner.FullName ||
                         srcInner.Name == borrowInner.Name))
                        return true;
                }
                // Bare entity T: accepted by both Accessing[T] and Controlling[T].
                if (source.Category == TypeCategory.Entity &&
                    (source.FullName == borrowInner.FullName ||
                     source.Name == borrowInner.Name ||
                     source.BareName == borrowInner.BareName))
                    return true;
            }

            return ImplementsProtocol(type: source, protocolName: target.Name);
        }


        // Const generic: `needs N is U64` means N is a U64 value at runtime.
        // Treat N as assignable to U64 (and vice versa).
        if (source is GenericParameterTypeInfo srcGen &&
            ConstGenericMatches(paramName: srcGen.Name, otherTypeName: target.Name))
            return true;
        if (target is GenericParameterTypeInfo tgtGen &&
            ConstGenericMatches(paramName: tgtGen.Name, otherTypeName: source.Name))
            return true;

        // None (Maybe generic def) is assignable to any Maybe[T]
        if (source is { IsGenericDefinition: true, Name: MaybeTypeName } && IsMaybeType(type: target))
            return true;

        // Entity, record, or wrapper type is implicitly assignable to Maybe[SameType].
        // Covers: entity fields, RC wrappers (Retained[T], Tracked[T] -> Maybe[Retained[T]]).
        if ((source.Category == TypeCategory.Entity || source.Category == TypeCategory.Record ||
             source.Category == TypeCategory.Wrapper) &&
            IsMaybeType(type: target) && target.TypeArguments is { Count: 1 })
        {
            TypeSymbol typeArg = target.TypeArguments[0];
            if (source.Name == typeArg.Name ||
                source.FullName == typeArg.FullName ||
                source.FullName == typeArg.Name ||
                source.Name == typeArg.FullName)
                return true;
            // Raw entity E -> Maybe[E]: rvalue entity auto-wraps into Owned, then carrier.
            // T is declared as `record T` in stdlib, so it surfaces as
            // RecordTypeInfo (not WrapperTypeInfo) at runtime — match by name + arity instead
            // of pattern-matching the runtime kind.
            if (source.Category == TypeCategory.Entity &&
                IsOwnedOf(type: typeArg, out TypeSymbol? ownedInnerOfMaybe) &&
                (source.Name == ownedInnerOfMaybe.Name ||
                 source.FullName == ownedInnerOfMaybe.FullName))
                return true;
        }

        // Raw entity E (rvalue) -> E: a freshly produced entity transfers ownership.
        if (source.Category == TypeCategory.Entity &&
            IsOwnedOf(type: target, out TypeSymbol? ownedInnerOfTarget) &&
            (source.Name == ownedInnerOfTarget.Name ||
             source.FullName == ownedInnerOfTarget.FullName))
            return true;

        // No implicit conversions - all type conversions must be explicit via creator syntax
        return false;
    }

    /// <summary>
    /// Returns true when <paramref name="type"/> represents <c>X</c> for some inner type
    /// <c>X</c>, regardless of whether the runtime kind is <see cref="WrapperTypeInfo"/>
    /// (legacy) or <see cref="RecordTypeInfo"/> (current — <c>Owned</c> is declared as
    /// <c>record T</c> in the stdlib, so most resolutions arrive as records). Resolutions
    /// of generic records carry their parameterized form in <see cref="TypeSymbol.Name"/>
    /// (e.g. <c>"Owned[Core.Text]"</c>), so we strip the bracket suffix before comparing.
    /// </summary>
    /// <summary>
    /// If <paramref name="type"/> is an ownership-carrying or borrow wrapper
    /// (Retained/Tracked/Modifying/Viewing/Controlling/Accessing/Hijacked) over some inner T,
    /// returns the base wrapper name and inner T. Returns false for anything else.
    /// </summary>
    private static bool TryGetOwnershipWrapperInner(TypeSymbol type, out string? wrapperBase,
        out TypeSymbol? inner)
    {
        string baseName = type.BareName;
        if (baseName is Compiler.Resolution.RuntimeContract.Retained or Compiler.Resolution.RuntimeContract.Tracked or Compiler.Resolution.RuntimeContract.Modifying or Compiler.Resolution.RuntimeContract.Viewing
            or Compiler.Resolution.RuntimeContract.Controlling or Compiler.Resolution.RuntimeContract.Accessing or Compiler.Resolution.RuntimeContract.Hijacked)
        {
            if (type is WrapperTypeInfo { InnerType: not null } w)
            {
                wrapperBase = baseName;
                inner = w.InnerType;
                return true;
            }
            if (type.TypeArguments is { Count: 1 } args)
            {
                wrapperBase = baseName;
                inner = args[index: 0];
                return true;
            }
        }
        wrapperBase = null;
        inner = null;
        return false;
    }

    private static bool IsOwnedOf(TypeSymbol type, out TypeSymbol inner)
    {
        if (type is WrapperTypeInfo { Name: Compiler.Resolution.RuntimeContract.Owned } wrapped)
        {
            inner = wrapped.InnerType;
            return true;
        }

        if (type.BareName == Compiler.Resolution.RuntimeContract.Owned &&
            type.TypeArguments is { Count: 1 } args)
        {
            inner = args[index: 0];
            return true;
        }

        inner = null!;
        return false;
    }

    /// <summary>
    /// Strips the generic-arg suffix from a RAW type-name string (e.g. "List[S64]" -> "List").
    /// Prefer <see cref="TypeInfo.BareName"/> when a TypeInfo is in hand; this exists only for the
    /// few call sites that carry a bare string (e.g. a <c>TypeExpression.Name</c> or a protocol-name
    /// parameter) with no TypeInfo to read <c>.BareName</c> from.
    /// </summary>
    private static string BareTypeName(string typeName)
    {
        return TypeInfo.StripTypeArgs(name: typeName);
    }

    /// <summary>Returns true if the type is the built-in <c>Bool</c> type.</summary>
    private static bool IsBoolType(TypeSymbol type)
    {
        return type.Name is "Bool";
    }

    /// <summary>Returns true if the type is any numeric type (integer, binary float, or decimal float).</summary>
    private bool IsNumericType(TypeSymbol type)
    {
        return IsIntegerType(type: type) || IsFloatType(type: type) || IsDecimalType(type: type);
    }

    /// <summary>
    /// Returns true if the type is a generic parameter whose constraint resolves to a numeric type
    /// (e.g., a const-generic <c>N</c> declared as <c>needs N is U64</c>) or a const-generic value
    /// whose explicit type is numeric. Such parameters carry a numeric value at each
    /// monomorphization and are acceptable wherever a numeric value is expected.
    /// </summary>
    private bool IsNumericGenericParam(TypeSymbol type)
    {
        if (type is ConstGenericValueTypeInfo) return true;

        if (type is GenericParameterTypeInfo gp)
        {
            string name = gp.Name;
            // Search the active generic-constraint scope for a numeric constraint on this name.
            // Constraints can live on the routine, the enclosing type, or — for
            // extension memberRoutines — on the routine's owner type (e.g., `Array[T,N]`
            // declares `needs N is U64`).
            List<List<GenericConstraintDeclaration>?> sources =
            [
                _currentRoutine?.GenericConstraints,
                _currentType?.GenericConstraints,
                _currentRoutine?.OwnerType?.GenericConstraints
            ];
            foreach (List<GenericConstraintDeclaration>? constraints in sources)
            {
                if (constraints == null) continue;
                foreach (GenericConstraintDeclaration c in constraints)
                {
                    if (c.ParameterName != name) continue;
                    if (c.ConstraintType != ConstraintKind.ConstGeneric) continue;
                    if (c.ConstraintTypes is not { Count: > 0 } types) continue;
                    foreach (TypeExpression boundExpr in types)
                    {
                        TypeSymbol? bound = LookupTypeWithImports(name: boundExpr.Name);
                        if (bound != null && IsNumericType(type: bound)) return true;
                    }
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Returns true if the type implements the <c>Integral</c> protocol (i.e., is a fixed-width or
    /// arbitrary-precision integer type such as s32, u64, uaddr, or Suflae's Integer).
    /// </summary>
    private bool IsIntegerType(TypeSymbol type)
    {
        // Check if type obeys the Integral protocol
        return ImplementsProtocol(type: type, protocolName: "Integral");
    }

    /// <summary>
    /// Returns true if the type implements the <c>BinaryFP</c> protocol (i.e., is a binary
    /// floating-point type such as f32 or f64).
    /// </summary>
    private bool IsFloatType(TypeSymbol type)
    {
        // Check if type obeys the Floating protocol (binary floats)
        return ImplementsProtocol(type: type, protocolName: "BinaryFP");
    }

    /// <summary>
    /// Returns true if the type implements the <c>DecimalFP</c> protocol (i.e., is a decimal
    /// floating-point type such as d64 or Suflae's Decimal).
    /// </summary>
    private bool IsDecimalType(TypeSymbol type)
    {
        // Check if type obeys the DecimalFloating protocol
        return ImplementsProtocol(type: type, protocolName: "DecimalFP");
    }

    /// <summary>Returns true if the type is a complex number type (C32, C64, C128, Complex).</summary>
    private static bool IsComplexType(TypeSymbol type)
    {
        return type.Name is "C32" or "C64" or "C128" or "Complex";
    }

    /// <summary>
    /// Checks if a type supports a specific binary operator by looking up the operator memberRoutine.
    /// </summary>
    /// <summary>
    /// Unwraps a transparent borrow protocol (<c>Accessing[T]</c> / <c>Controlling[T]</c>) to its
    /// referent <c>T</c>; returns the type unchanged otherwise. Used so comparison operands that are
    /// borrows are treated as their referent (the operator auto-dispatches <c>refer</c>/<c>control</c>).
    /// </summary>
    private static TypeSymbol UnwrapBorrowProtocol(TypeSymbol type)
    {
        if (type.Category == TypeCategory.Protocol &&
            type.BareName is Compiler.Resolution.RuntimeContract.Accessing or Compiler.Resolution.RuntimeContract.Controlling &&
            type.TypeArguments is { Count: > 0 } args)
        {
            return args[index: 0];
        }

        return type;
    }

    private bool SupportsOperator(TypeSymbol type, BinaryOperator op)
    {
        // Check the BASE wired memberRoutine, not the derived one: `!=`/`==` are both backed by `eq`
        // (ne is auto-derived from eq), and all ordering operators are backed by `cmp`
        // (lt/le/gt/ge are auto-derived). Protocols (Equatable/Comparable) declare only the
        // base memberRoutine, so checking the derived name would spuriously fail for constrained generics
        // (e.g. `me[i] != other[i]` inside `List[T].eq needs T obeys Equatable`).
        string? memberRoutineName = op switch
        {
            BinaryOperator.Equal or BinaryOperator.NotEqual => "eq",
            BinaryOperator.Less or BinaryOperator.LessEqual or BinaryOperator.Greater
                or BinaryOperator.GreaterEqual or BinaryOperator.ThreeWayComparator => "cmp",
            _ => op.GetMemberRoutineName()
        };
        if (memberRoutineName == null)
        {
            return false;
        }

        // Use LookupMemberRoutine which handles generic resolutions (e.g., Hijacked[Point].eq).
        // A resolution whose owner is a ProtocolTypeInfo is the ABSTRACT protocol declaration
        // (RF protocols have no default implementations) — for a CONCRETE receiver it would link
        // to nothing (e.g. `record Cat` with no `eq` resolving `==` to `Equatable.eq`). Only a
        // concrete implementation counts as support here; generic-parameter receivers get their
        // constraint-based support from the dedicated branch below.
        RoutineInfo? resolved = _registry.LookupMemberRoutine(type: type, memberRoutineName: memberRoutineName);
        if (resolved != null && resolved.OwnerType is not ProtocolTypeInfo)
            return true;

        // Phase D: transparent wrappers (T, etc.) forward operator wired memberRoutines
        // to the inner T's implementation. Synthesize the forwarder lazily.
        if (IsWrapperType(type: type) &&
            TrySynthesizeWrapperForwarder(wrapperType: type, memberRoutineName: memberRoutineName,
                isFailable: false) != null)
            return true;

        // For generic parameters, check if any constrained protocol declares the memberRoutine.
        if (type is GenericParameterTypeInfo)
        {
            foreach (GenericConstraintDeclaration c in ActiveConstraintsFor(paramName: type.Name))
            {
                if (c is { ConstraintType: ConstraintKind.Obeys, ConstraintTypes: not null })
                {
                    foreach (TypeExpression protocolExpr in c.ConstraintTypes)
                    {
                        TypeSymbol? proto = _registry.LookupType(name: protocolExpr.Name);
                        if (proto is ProtocolTypeInfo &&
                            ProtocolDeclaresMemberRoutine(proto: proto, memberRoutineName: memberRoutineName))
                            return true;
                    }
                }
                // `needs N is U64` — operator support follows the underlying value type.
                else if (c is { ConstraintType: ConstraintKind.ConstGeneric, ConstraintTypes: not null })
                {
                    foreach (TypeExpression ct in c.ConstraintTypes)
                    {
                        TypeSymbol? underlying = _registry.LookupType(name: ct.Name);
                        if (underlying != null &&
                            underlying.Category != TypeCategory.Protocol &&
                            _registry.LookupMemberRoutine(type: underlying, memberRoutineName: memberRoutineName) != null)
                            return true;
                    }
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Returns true if the protocol (or any protocol it transitively obeys) declares a memberRoutine
    /// matching the given name. e.g. <c>Comparable</c> obeys <c>Equatable</c>, so
    /// <c>eq</c> is reachable through <c>Comparable</c>.
    /// </summary>
    private static bool ProtocolDeclaresMemberRoutine(TypeSymbol proto, string memberRoutineName,
        HashSet<string>? visited = null)
    {
        if (proto is not ProtocolTypeInfo p) return false;
        visited ??= new HashSet<string>(StringComparer.Ordinal);
        if (!visited.Add(item: p.Name)) return false;
        if (p.MemberRoutines.Any(predicate: m => m.Name == memberRoutineName))
            return true;
        return p.ParentProtocols.Any(parent =>
            ProtocolDeclaresMemberRoutine(proto: parent, memberRoutineName: memberRoutineName, visited: visited));
    }

    /// <summary>
    /// Returns true if the named generic parameter has a `is &lt;TypeName&gt;` const-generic
    /// constraint matching <paramref name="otherTypeName"/>. Note: ConstraintKind.ConstGeneric is
    /// also used for `is EntityType`/`is RecordType` etc., which don't represent value types —
    /// callers handle those via Category checks first.
    /// </summary>
    private bool ConstGenericMatches(string paramName, string otherTypeName)
    {
        return ActiveConstraintsFor(paramName: paramName)
            .Where(c => c is { ConstraintType: ConstraintKind.ConstGeneric, ConstraintTypes: not null })
            .Any(c => c.ConstraintTypes!.Any(ct => ct.Name == otherTypeName));
    }

    /// <summary>
    /// Yields all active generic constraints for the named parameter from the current routine
    /// and its owner type.
    /// </summary>
    private IEnumerable<GenericConstraintDeclaration> ActiveConstraintsFor(string paramName)
    {
        if (_currentRoutine?.GenericConstraints != null)
        {
            foreach (GenericConstraintDeclaration c in _currentRoutine.GenericConstraints)
                if (c.ParameterName == paramName) yield return c;
        }

        TypeSymbol? ownerType = _currentRoutine?.OwnerType;
        if (ownerType?.GenericConstraints != null)
        {
            foreach (GenericConstraintDeclaration c in ownerType.GenericConstraints)
                if (c.ParameterName == paramName) yield return c;
        }
    }

    /// <summary>
    /// Checks if an operator is a comparison operator that returns Bool.
    /// Includes both identity operators and overloadable comparison/membership operators.
    /// Note: ThreeWayComparator (&lt;=&gt;) returns ComparisonSign, not Bool, so it is excluded.
    /// </summary>
    private static bool IsComparisonOperator(BinaryOperator op)
    {
        return op is BinaryOperator.Equal or BinaryOperator.NotEqual or BinaryOperator.Less
            or BinaryOperator.LessEqual or BinaryOperator.Greater or BinaryOperator.GreaterEqual
            or BinaryOperator.In or BinaryOperator.NotIn or BinaryOperator.Is or BinaryOperator.IsNot
            or BinaryOperator.Obeys or BinaryOperator.Disobeys;
    }

    /// <summary>Returns true if the operator is a short-circuit logical operator (<c>and</c> or <c>or</c>).</summary>
    private static bool IsLogicalOperator(BinaryOperator op)
    {
        return op is BinaryOperator.And or BinaryOperator.Or;
    }

    private static bool IsShiftOperator(BinaryOperator op)
    {
        return op is BinaryOperator.ArithmeticLeftShift or BinaryOperator.ArithmeticRightShift
            or BinaryOperator.LogicalLeftShift or BinaryOperator.LogicalRightShift;
    }

    /// <summary>
    /// Operator wired memberRoutines that choices are NOT allowed to define or call.
    /// Choices do not support any operators — use 'is' for case matching.
    /// </summary>
    private static readonly HashSet<string> OperatorWiredMemberRoutines =
    [
        // Arithmetic
        "add", "sub", "mul", "truediv", "floordiv", "mod", "pow",
        // Wrapping arithmetic
        "add_wrap", "sub_wrap", "mul_wrap", "pow_wrap",
        // Clamping arithmetic
        "add_clamp", "sub_clamp", "mul_clamp", "truediv_clamp", "pow_clamp",
        // Comparison
        "eq", "ne", "lt", "le", "gt", "ge", "cmp",
        // Bitwise
        "bitand", "bitor", "bitxor",
        "ashl", "ashr", "lshl", "lshr",
        // Unary
        "neg", "bitnot",
        // Membership
        "contains", "notcontains",
        // Indexing
        "getitem", "setitem",
        // Iteration
        "iter", "emit",
        // Context management
        "enter", "exit"
    ];

    /// <summary>Returns true if the given memberRoutine name is an operator wired (e.g., <c>add</c>, <c>eq</c>).</summary>
    private static bool IsOperatorWired(string name)
    {
        return OperatorWiredMemberRoutines.Contains(value: name);
    }

    /// <summary>
    /// Validates comparison operands for type compatibility and operator support.
    /// Called from both AnalyzeBinaryExpression (for non-desugared operators like is, obeys)
    /// and AnalyzeChainedComparisonExpression (for chained comparisons like a &lt; b &lt; c).
    /// </summary>
    private void ValidateComparisonOperands(TypeSymbol left, TypeSymbol right, BinaryOperator op,
        SourceLocation location)
    {
        // A `Accessing[T]` / `Controlling[T]` operand is a borrow that transparently forwards to its
        // referent: comparing it auto-dispatches `refer()` / `control()` to the inner `T`. Compare
        // against that referent so e.g. `me[i] == value` (with `value: Accessing[T]`) type-checks as
        // `T == T` and resolves operator support on `T`.
        left = UnwrapBorrowProtocol(type: left);
        right = UnwrapBorrowProtocol(type: right);

        // Variants cannot use equality or ordering operators (only 'is' and 'isnot')
        if (left.Category == TypeCategory.Variant || right.Category == TypeCategory.Variant)
        {
            if (op is not (BinaryOperator.Is or BinaryOperator.IsNot))
            {
                ReportError(code: SemanticDiagnosticCode.ComparisonOnVariantType,
                    message:
                    $"Comparison operator '{op.ToStringRepresentation()}' cannot be used with variant types. Use 'is' or 'isnot' for pattern matching.",
                    location: location);
            }

            return;
        }

        // Membership operators (in, notin): check that right has contains accepting left
        if (op is BinaryOperator.In or BinaryOperator.NotIn)
        {
            RoutineInfo? containsMemberRoutine =
                _registry.LookupMemberRoutine(type: right, memberRoutineName: "contains");
            if (containsMemberRoutine == null)
            {
                ReportError(code: SemanticDiagnosticCode.IncompatibleComparisonTypes,
                    message:
                    $"Type '{right.Name}' does not support 'in'/'notin' (no contains member routine).",
                    location: location);
            }

            return;
        }

        // Check that types are compatible (same type or error type)
        if (!IsAssignableTo(source: left, target: right) &&
            !IsAssignableTo(source: right, target: left))
        {
            ReportError(code: SemanticDiagnosticCode.IncompatibleComparisonTypes,
                message:
                $"Cannot compare values of incompatible types '{left.Name}' and '{right.Name}'.",
                location: location);
        }

        // For overloadable ordering/equality operators, verify the type actually implements the
        // backing wired memberRoutine (eq for ==/!=, cmp for </<=/>/>=). These are desugared to memberRoutine
        // calls by OperatorLoweringPass (after SA), so without this check an unsupported operator
        // would slip past SA and surface as an undefined-symbol LINKERR at codegen — e.g. a record
        // with no eq whose `==` resolves to the abstract `Equatable.eq`. A LINKERR on SA-passing
        // code is a compiler bug; catch it here with a clean diagnostic.
        if (op is not (BinaryOperator.Less or BinaryOperator.LessEqual or BinaryOperator.Greater
            or BinaryOperator.GreaterEqual or BinaryOperator.Equal or BinaryOperator.NotEqual))
        {
            return;
        }

        if (!SupportsOperator(type: left, op: op))
        {
            ReportError(code: SemanticDiagnosticCode.OrderingNotSupported,
                message:
                $"Type '{left.Name}' does not support comparison operator '{op.ToStringRepresentation()}'.",
                location: location);
        }
    }

    /// <summary>
    /// Validates that a chained comparison expression uses operators in a consistent direction.
    /// Valid patterns:
    /// - All ascending: a &lt; b &lt; c, a &lt;= b &lt; c, a == b &lt; c
    /// - All descending: a &gt; b &gt; c, a &gt;= b &gt; c, a == b &gt; c
    /// - Equality only: a == b == c
    /// Invalid: mixing ascending and descending (a &lt; b &gt; c)
    /// </summary>
    private void ValidateComparisonChain(ChainedComparisonExpression chain,
        SourceLocation location) // NOSONAR S3776
    {
        if (chain.Operators.Count < 2)
        {
            return; // No chain to validate
        }

        bool? isAscending = null;

        foreach (BinaryOperator op in chain.Operators)
        {
            // Equality operators are direction-neutral
            if (op == BinaryOperator.Equal)
            {
                continue;
            }

            // NotEqual cannot be used in chains
            if (op == BinaryOperator.NotEqual)
            {
                ReportError(code: SemanticDiagnosticCode.NotEqualInComparisonChain,
                    message: "The '!=' operator cannot be used in comparison chains.",
                    location: location);
                return;
            }

            bool opIsAscending = op is BinaryOperator.Less or BinaryOperator.LessEqual;
            bool opIsDescending = op is BinaryOperator.Greater or BinaryOperator.GreaterEqual;

            if (opIsAscending)
            {
                if (isAscending == false)
                {
                    ReportError(code: SemanticDiagnosticCode.MixedComparisonChainDirection,
                        message:
                        "Cannot mix ascending (<, <=) and descending (>, >=) operators in a comparison chain.",
                        location: location);
                    return;
                }

                isAscending = true;
            }
            else if (opIsDescending)
            {
                if (isAscending == true)
                {
                    ReportError(code: SemanticDiagnosticCode.MixedComparisonChainDirection,
                        message:
                        "Cannot mix ascending (<, <=) and descending (>, >=) operators in a comparison chain.",
                        location: location);
                    return;
                }

                isAscending = false;
            }
        }
    }

    /// <summary>
    /// Resolves the element type produced by iterating over <paramref name="iterableType"/>.
    /// The type must implement the <c>Iterable</c> protocol, whose <c>iter</c> returns a <c>Iterator[T]</c>.
    /// The element type is taken from the return type of the <c>iter</c> memberRoutine or the type's first generic argument.
    /// Reports an error and returns <see cref="ErrorTypeInfo"/> if the type is not iterable or the element type cannot be determined.
    /// </summary>
    private TypeSymbol GetIterableElementType(TypeSymbol iterableType, SourceLocation location)
    {
        // Marker-protocol unwrap: `Accessing[X]` / `Controlling[X]` are transparent
        // pass-throughs to X. If iterating one, dispatch to X's Iterable conformance.
        if (TryGetTransparentProtocolTarget(type: iterableType,
            targetType: out TypeSymbol unwrapped))
        {
            iterableType = unwrapped;
        }

        // Protocol-typed receiver: if the static type IS `Iterable[T]` (or a
        // protocol that obeys Iterable), trust the dispatch and take the
        // element type from the type-arg. Any concrete value bound will
        // implement Iterable structurally.
        if (iterableType is ProtocolTypeInfo iproto)
        {
            string baseName = (iproto.GenericDefinition ?? iproto).BareName;
            if (baseName == "Iterable" && iproto.TypeArguments is { Count: > 0 })
            {
                return iproto.TypeArguments[index: 0];
            }
        }

        // Generic-parameter receiver constrained to `Iterable[X]` — e.g. a desugared protocol
        // parameter `r: Iterable[S64]` becomes `__T0 obeys Iterable[S64]`. The constraint pins the
        // element type to X for EVERY instantiation, so take the element from the obeys-constraint's
        // type argument. Without this the element falls through to Strategy 2 and resolves to the
        // protocol's bare element param `T`, which then leaks unsubstituted into the monomorphized
        // body and reaches codegen (`GenericParameterTypeInfo 'T' reached GetLlvmType`).
        if (iterableType is GenericParameterTypeInfo gp)
        {
            foreach (GenericConstraintDeclaration c in ActiveConstraintsFor(paramName: gp.Name))
            {
                if (c is not { ConstraintType: ConstraintKind.Obeys, ConstraintTypes: not null })
                    continue;
                foreach (TypeExpression protocolExpr in c.ConstraintTypes)
                {
                    if (BareTypeName(typeName: protocolExpr.Name) != "Iterable") continue;
                    TypeSymbol resolved = _typeResolver.ResolveType(typeExpr: protocolExpr);
                    if (resolved.TypeArguments is { Count: > 0 })
                        return resolved.TypeArguments[index: 0];
                }
            }
        }

        // Type must follow the Iterable protocol
        bool obeysIterable = ImplementsProtocol(type: iterableType, protocolName: "Iterable");

        // For generic resolution types, also check if the generic definition has iter
        if (!obeysIterable && iterableType.IsGenericResolution)
        {
            RoutineInfo? seqMemberRoutine =
                _registry.LookupMemberRoutine(type: iterableType, memberRoutineName: "iter");
            if (seqMemberRoutine != null)
            {
                obeysIterable = true;
            }
        }

        if (!obeysIterable)
        {
            ReportError(code: SemanticDiagnosticCode.TypeNotIterable,
                message: $"Type '{iterableType.Name}' is not iterable. Types must follow the " +
                         $"'Iterable' protocol to be used in for-in loops.",
                location: location);
            return ErrorTypeInfo.Instance;
        }

        // Strategy 1: Extract element type from Iterable[X] protocol conformance.
        // This correctly handles chained generics like EnumerateIterator[T] obeys Iterable[Tuple[S64, T]]
        List<TypeSymbol>? protocols = iterableType switch
        {
            RecordTypeInfo record => record.ImplementedProtocols,
            EntityTypeInfo entity => entity.ImplementedProtocols,
            _ => null
        };

        if (protocols != null)
        {
            foreach (TypeSymbol proto in protocols)
            {
                if (proto.BareName == "Iterable" &&
                    proto.TypeArguments is { Count: > 0 })
                {
                    TypeInfo elementType = proto.TypeArguments[index: 0];

                    // Resolve generic parameters if the iterable is a generic resolution
                    if (iterableType is { IsGenericResolution: true, TypeArguments: not null })
                    {
                        TypeInfo? genericDef = iterableType switch
                        {
                            RecordTypeInfo r => r.GenericDefinition,
                            EntityTypeInfo e => e.GenericDefinition,
                            _ => null
                        };
                        if (genericDef?.GenericParameters != null)
                        {
                            var substitution = new Dictionary<string, TypeInfo>();
                            for (int i = 0;
                                 i < genericDef.GenericParameters.Count &&
                                 i < iterableType.TypeArguments.Count;
                                 i++)
                            {
                                substitution[key: genericDef.GenericParameters[index: i]] =
                                    iterableType.TypeArguments[index: i];
                            }

                            elementType = SubstituteTypeParams(type: elementType,
                                substitution: substitution);
                        }
                    }

                    return elementType;
                }
            }
        }

        // Strategy 1.5 (ground truth): the element is exactly what the iterator's `emit!` returns.
        // Resolve `iterable.iter()` to the concrete iterator type, then that iterator's `emit!`
        // return type. This mirrors the for-loop lowering (IteratorInlineLoweringPass) and, unlike
        // Strategy 1, does NOT depend on the instance's ImplementedProtocols being populated — so it
        // works for a generic-instance collection (e.g. `Dict[Text, SerialValue]`) iterated inside a
        // CONCRETE memberRoutine compiled before that instance is monomorphized. There, ImplementedProtocols
        // is still empty and the naive `TypeArguments[0]` fallback below would wrongly pick the first
        // type arg (`K`, i.e. `Text` for a Dict) instead of `DictEntry[Text, SerialValue]`.
        RoutineInfo? iterMemberRoutine = _registry.LookupMemberRoutine(type: iterableType, memberRoutineName: "iter");
        if (iterMemberRoutine?.ReturnType is { } iteratorType and not ErrorTypeInfo)
        {
            RoutineInfo? emitMemberRoutine =
                _registry.LookupMemberRoutine(type: iteratorType, memberRoutineName: "emit", isFailable: true);
            if (emitMemberRoutine?.ReturnType is { } emittedType and not (ErrorTypeInfo or GenericParameterTypeInfo))
            {
                return emittedType;
            }
        }

        // Strategy 2: Look for iter memberRoutine to get element type from Iterator[T] return type
        RoutineInfo? seqMemberRoutine2 = _registry.LookupRoutine(fullName: $"{iterableType.Name}.iter");

        // Generic fallback: Range[S64].iter -> Range.iter via LookupMemberRoutine
        if (seqMemberRoutine2 == null)
        {
            seqMemberRoutine2 = _registry.LookupMemberRoutine(type: iterableType, memberRoutineName: "iter");
        }

        if (seqMemberRoutine2?.ReturnType?.TypeArguments is { Count: > 0 })
        {
            // Resolve generic type args: if return type arg is T and iterableType is Range[S64], resolve T -> S64
            TypeInfo returnTypeArg = seqMemberRoutine2.ReturnType.TypeArguments[index: 0];
            if (returnTypeArg is GenericParameterTypeInfo && iterableType is
                    { IsGenericResolution: true, TypeArguments: not null })
            {
                TypeInfo? genericDef = iterableType switch
                {
                    RecordTypeInfo r => r.GenericDefinition,
                    EntityTypeInfo e => e.GenericDefinition,
                    _ => null
                };
                if (genericDef?.GenericParameters != null)
                {
                    int paramIndex = genericDef.GenericParameters
                                               .ToList()
                                               .IndexOf(item: returnTypeArg.Name);
                    if (paramIndex >= 0 && paramIndex < iterableType.TypeArguments.Count)
                    {
                        return iterableType.TypeArguments[index: paramIndex];
                    }
                }
            }

            return returnTypeArg;
        }

        // Fallback to type arguments if iter memberRoutine not found but protocol is implemented
        if (iterableType.TypeArguments is { Count: > 0 })
        {
            return iterableType.TypeArguments[index: 0];
        }

        ReportError(code: SemanticDiagnosticCode.TypeNotIterable,
            message:
            $"Cannot determine element type for '{iterableType.Name}'. The iter member routine must return Iterator[T].",
            location: location);
        return ErrorTypeInfo.Instance;
    }

    #endregion

    /// <summary>
    /// If <paramref name="paramType"/> is a marker protocol (Accessing[T]/Controlling[T])
    /// and the argument isn't already an in-flight inner T, wraps the argument expression
    /// as `arg.refer()` or `arg.control()`. The resulting CallExpression has
    /// ResolvedRoutine and ResolvedType set so downstream passes (reachability, codegen,
    /// CallOverloadResolutionPass) treat it as a normal resolved memberRoutine call.
    /// </summary>
    private void TryInjectMarkerCoercion(RoutineInfo routine, List<Expression> arguments,
        int paramIndex, TypeSymbol paramType, TypeSymbol argType)
    {
        if (paramType is not ProtocolTypeInfo { TypeArguments: { Count: > 0 } } proto)
            return;

        ProtocolTypeInfo def = proto.GenericDefinition ?? proto;
        string baseName = def.BareName;
        string memberRoutineName;
        if (baseName == Compiler.Resolution.RuntimeContract.Controlling) memberRoutineName = "control";
        else if (baseName == Compiler.Resolution.RuntimeContract.Accessing) memberRoutineName = "refer";
        else return;

        // Pass-through: the argument is already typed as the same marker protocol. No
        // coercion call is needed — Accessing[T] is layout-compatible with inner T, so
        // the rewritten signature (T param) accepts it directly. Phase 8's expression
        // ResolvedType sweep retags the argument to inner T so codegen sees no marker.
        if (argType is ProtocolTypeInfo argProto
            && ReferenceEquals(argProto.GenericDefinition ?? argProto, def))
        {
            return;
        }

        TypeSymbol innerT = proto.TypeArguments![0]!;

        // Locate the argument slot in `arguments`.
        int slotIndex = -1;
        Expression slotExpr = null!;
        for (int i = 0; i < arguments.Count; i++)
        {
            Expression a = arguments[i];
            if (a is NamedArgumentExpression na && na.Name == routine.Parameters[paramIndex].Name)
            {
                slotIndex = i;
                slotExpr = a;
                break;
            }
        }
        if (slotIndex < 0)
        {
            // Positional: argument position equals paramIndex when no named args precede it.
            // Walk arguments to find the positional slot at paramIndex.
            int pos = 0;
            for (int i = 0; i < arguments.Count; i++)
            {
                if (arguments[i] is NamedArgumentExpression) continue;
                if (pos == paramIndex) { slotIndex = i; slotExpr = arguments[i]; break; }
                pos++;
            }
        }
        if (slotIndex < 0) return;

        Expression inner = slotExpr is NamedArgumentExpression nx ? nx.Value : slotExpr;

        // Skip if already coerced.
        if (inner is CallExpression { Callee: MemberExpression { MemberName: "access" or "control" } })
            return;

        // Resolve the memberRoutine on the source argument type.
        RoutineInfo? coercion = _registry.LookupMemberRoutineOverload(type: argType,
            memberRoutineName: memberRoutineName, argTypes: []);
        coercion ??= _registry.LookupMemberRoutine(type: argType, memberRoutineName: memberRoutineName);
        if (coercion == null) return;

        var memberCallee = new MemberExpression(
            Object: inner,
            MemberName: memberRoutineName,
            Location: inner.Location);
        var coerced = new CallExpression(
            Callee: memberCallee,
            Arguments: [],
            Location: inner.Location)
        {
            ResolvedRoutine = coercion,
            ResolvedType = innerT,
            IsInFlight = true,
            IsSynthesizedLowering = true,
            LoweringKind = CallClassifier.ClassifyMemberRoutineCall(memberRoutine: coercion)
        };

        arguments[slotIndex] = slotExpr is NamedArgumentExpression na2
            ? na2 with { Value = coerced }
            : coerced;
    }

    /// <summary>
    /// True if `type` references any name listed in `genericParameters` via a
    /// `GenericParameterTypeInfo` — i.e. an unresolved memberRoutine-level generic param.
    /// Used to suppress premature argument-type errors before generic inference runs.
    /// </summary>
    private static bool ContainsUnresolvedMemberRoutineGeneric(TypeSymbol type,
        List<string>? genericParameters)
    {
        if (genericParameters is null || genericParameters.Count == 0)
        {
            return false;
        }

        if (type is GenericParameterTypeInfo gp && genericParameters.Contains(item: gp.Name))
        {
            return true;
        }

        if (type.TypeArguments is { Count: > 0 } args)
        {
            foreach (TypeSymbol arg in args)
            {
                if (ContainsUnresolvedMemberRoutineGeneric(type: arg,
                        genericParameters: genericParameters))
                {
                    return true;
                }
            }
        }

        if (type is RoutineTypeInfo routine)
        {
            foreach (TypeSymbol pt in routine.ParameterTypes)
            {
                if (ContainsUnresolvedMemberRoutineGeneric(type: pt,
                        genericParameters: genericParameters))
                {
                    return true;
                }
            }

            if (routine.ReturnType is { } ret &&
                ContainsUnresolvedMemberRoutineGeneric(type: ret,
                    genericParameters: genericParameters))
            {
                return true;
            }
        }

        return false;
    }
}
