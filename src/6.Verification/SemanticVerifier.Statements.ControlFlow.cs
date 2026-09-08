using Compiler.Diagnostics;
using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;
using Compiler.Verification.Enums;

namespace Compiler.Verification;

using TypeSymbol = TypeInfo;

public sealed partial class SemanticVerifier
{
    private void AnalyzeIfStatement(IfStatement ifStmt)
    {
        TypeSymbol conditionType = AnalyzeExpression(expression: ifStmt.Condition);

        // Condition must be boolean. An ErrorTypeInfo condition is either a sub-expression that already
        // reported its own error (piling "must be boolean" on top is pure cascade noise) OR a comptime
        // splice deferred to monomorphization — e.g. `if me.${m.name}.is_none()` inside an `expand`, where
        // the splice-member is ErrorType pre-monomorph and the real Bool only exists per concrete field.
        // Either way, suppress the boolean check for ErrorType and let the deferred/errored path settle.
        if (!IsBoolType(type: conditionType) && conditionType is not ErrorTypeInfo)
        {
            ReportError(code: SemanticDiagnosticCode.IfConditionNotBool,
                message: $"If condition must be boolean, got '{conditionType.Name}'.",
                location: ifStmt.Condition.Location);
        }

        // Extract narrowing info from condition: carrier/None (NarrowingInfo) + general user variant
        // (VariantIsNarrowing, which accumulates arm exclusions down an if/elseif chain).
        NarrowingInfo? narrowing = TryExtractNarrowingFromCondition(condition: ifStmt.Condition);
        VariantIsNarrowing? variantNarrowing = TryGetVariantIsNarrowing(condition: ifStmt.Condition);

        // ── Deadref (steal) flow across the branches ──────────────────────────────
        // `steal x` marks x dead for the REST of the current linear scope (_deadrefVariables
        // is flow-insensitive). But a steal in a branch that definitely EXITS
        // (return/throw/break/continue) must NOT leak onto the path taken when that branch did
        // not run — e.g. `var v = ...; if c: return steal v; use(v)` keeps `v` alive on the
        // fall-through. Snapshot the dead set, analyze each branch from the same pre-if state,
        // then keep only the deadrefs reaching past the `if` (from branches that fall through).
        var deadrefBefore = new HashSet<string>(collection: _deadrefVariables);

        // Analyze then branch (with narrowing if applicable)
        AnalyzeIfThenBranch(ifStmt: ifStmt, narrowing: narrowing,
            variantNarrowing: variantNarrowing);

        bool thenExits = HasDefiniteExit(statement: ifStmt.ThenStatement);
        var afterThen = new HashSet<string>(collection: _deadrefVariables);
        // Re-analyze the else branch from the pre-if dead set — the branches are mutually
        // exclusive, so the else must not see the then branch's steals.
        _deadrefVariables.Clear();
        _deadrefVariables.UnionWith(other: deadrefBefore);

        // Analyze else branch if present (with inverse narrowing if applicable)
        if (ifStmt.ElseStatement != null)
        {
            AnalyzeIfElseBranch(ifStmt: ifStmt, narrowing: narrowing,
                variantNarrowing: variantNarrowing);
        }

        bool elseExits = ifStmt.ElseStatement != null &&
                         HasDefiniteExit(statement: ifStmt.ElseStatement);
        var afterElse = new HashSet<string>(collection: _deadrefVariables);
        // Merge: a variable is dead after the `if` iff it is dead on some path that FALLS
        // THROUGH. Steals confined to an exiting branch are dropped. (With no else branch,
        // `afterElse` is the pre-if state, i.e. the condition-false fall-through.)
        _deadrefVariables.Clear();
        if (!thenExits)
        {
            _deadrefVariables.UnionWith(other: afterThen);
        }
        if (!elseExits)
        {
            _deadrefVariables.UnionWith(other: afterElse);
        }

        // Guard clause narrowing: if the then branch definitely exits,
        // apply else narrowing to the remainder of the current scope
        if (ifStmt.ElseStatement == null && HasDefiniteExit(statement: ifStmt.ThenStatement))
        {
            ApplyGuardClauseNarrowing(narrowing: narrowing, variantNarrowing: variantNarrowing);
        }
    }

    /// <summary>
    /// Analyzes the then-branch of an if statement, opening a narrowing scope when the condition
    /// narrows a variable's type / non-null state / variant arm.
    /// </summary>
    private void AnalyzeIfThenBranch(IfStatement ifStmt, NarrowingInfo? narrowing,
        VariantIsNarrowing? variantNarrowing)
    {
        if (narrowing?.ThenBranchType != null || narrowing is { ThenNonNull: true } ||
            variantNarrowing != null)
        {
            _registry.EnterScope(kind: ScopeKind.Block, name: "if_then");
            if (narrowing?.ThenBranchType != null)
            {
                _registry.NarrowVariable(name: narrowing.VariableName,
                    narrowedType: narrowing.ThenBranchType);
            }

            if (narrowing is { ThenNonNull: true })
            {
                _registry.MarkVariableNonNull(name: narrowing.VariableName);
            }

            if (variantNarrowing != null)
            {
                ApplyVariantNarrowing(vn: variantNarrowing, conditionTrue: true);
            }

            AnalyzeStatement(statement: ifStmt.ThenStatement);
            _registry.ExitScope();
        }
        else
        {
            AnalyzeStatement(statement: ifStmt.ThenStatement);
        }
    }

    /// <summary>
    /// Analyzes the else-branch of an if statement, opening an inverse-narrowing scope when the
    /// condition narrows a variable's type / non-null state / variant arm.
    /// </summary>
    private void AnalyzeIfElseBranch(IfStatement ifStmt, NarrowingInfo? narrowing,
        VariantIsNarrowing? variantNarrowing)
    {
        if (narrowing?.ElseBranchType != null || narrowing is { ElseNonNull: true } ||
            variantNarrowing != null)
        {
            _registry.EnterScope(kind: ScopeKind.Block, name: "if_else");
            if (narrowing?.ElseBranchType != null)
            {
                _registry.NarrowVariable(name: narrowing.VariableName,
                    narrowedType: narrowing.ElseBranchType);
            }

            if (narrowing is { ElseNonNull: true })
            {
                _registry.MarkVariableNonNull(name: narrowing.VariableName);
            }

            if (variantNarrowing != null)
            {
                ApplyVariantNarrowing(vn: variantNarrowing, conditionTrue: false);
            }

            AnalyzeStatement(statement: ifStmt.ElseStatement!);
            _registry.ExitScope();
        }
        else
        {
            AnalyzeStatement(statement: ifStmt.ElseStatement!);
        }
    }

    /// <summary>
    /// Applies inverse (else) narrowing to the remainder of the current scope for a guard-clause
    /// if (no else branch, then-branch definitely exits).
    /// </summary>
    private void ApplyGuardClauseNarrowing(NarrowingInfo? narrowing,
        VariantIsNarrowing? variantNarrowing)
    {
        if (narrowing?.ElseBranchType != null)
        {
            _registry.NarrowVariable(name: narrowing.VariableName,
                narrowedType: narrowing.ElseBranchType);
        }

        if (narrowing is { ElseNonNull: true })
        {
            _registry.MarkVariableNonNull(name: narrowing.VariableName);
        }

        if (variantNarrowing != null)
        {
            ApplyVariantNarrowing(vn: variantNarrowing, conditionTrue: false);
        }
    }

    private void AnalyzeWhileStatement(WhileStatement whileStmt)
    {
        TypeSymbol conditionType = AnalyzeExpression(expression: whileStmt.Condition);

        // Condition must be boolean (ErrorType suppressed as cascade / comptime-splice deferral — see
        // AnalyzeIfStatement for the rationale).
        if (!IsBoolType(type: conditionType) && conditionType is not ErrorTypeInfo)
        {
            ReportError(code: SemanticDiagnosticCode.WhileConditionNotBool,
                message: $"While condition must be boolean, got '{conditionType.Name}'.",
                location: whileStmt.Condition.Location);
        }

        // Analyze loop body
        _registry.EnterScope(kind: ScopeKind.Loop, name: "while");
        AnalyzeStatement(statement: whileStmt.Body);
        _registry.ExitScope();
    }

    /// <summary>
    /// Analyzes a comptime <c>expand m in allmemvarof(T)</c> loop. Because the concrete members of
    /// <c>T</c> are unknown until monomorphization, this does NOT unroll or fully type the body —
    /// it only validates the template shape: the source type resolves, the handle <c>m</c> is
    /// registered as a comptime-handle sentinel (so <c>m.name</c>/<c>m.id</c> and the splices type
    /// leniently), and the body is analyzed once. The real per-member expansion and typecheck
    /// happen in the generic AST rewriter at instantiation.
    /// </summary>
    private void AnalyzeExpandStatement(ExpandStatement expandStmt)
    {
        // BuilderExpansion gate: `expand` and its sources (allmemvarof/openmemvarof/caseof/branchof) are
        // comptime intrinsics housed in the BuilderExpansion module — no longer keywords, siblings of
        // nameof/typeof. Using them requires the opt-in import (mirrors `import BuilderQuery`).
        if (!_importedModules.Contains(item: "BuilderExpansion"))
            ReportError(code: SemanticDiagnosticCode.BuilderExpansionImportRequired,
                message: "'expand' requires 'import BuilderExpansion'.",
                location: expandStmt.Location);

        // The parser is name-agnostic (it stored whatever identifier followed `in`); validate here that it
        // is a real reflection source.
        if (!ExpandSources.IsSource(name: expandStmt.SourceName))
            ReportError(code: SemanticDiagnosticCode.UnknownExpandSource,
                message:
                $"'{expandStmt.SourceName}' is not a valid expand source — use 'allmemvarof', 'openmemvarof', 'caseof', or 'branchof'.",
                location: expandStmt.Location);

        // Expansion is single-level: a member walk cannot itself contain a member walk.
        if (_inExpandBody)
            ReportError(code: SemanticDiagnosticCode.NestedExpandNotAllowed,
                message:
                "An 'expand' cannot be nested inside another 'expand' — comptime member expansion is single-level.",
                location: expandStmt.Location);

        _registry.EnterScope(kind: ScopeKind.Loop, name: "expand");

        // Resolve the source type for early error surfacing (a bare generic param resolves fine).
        // The result is intentionally unused — no member walk happens before monomorphization.
        ResolveType(typeExpr: expandStmt.SourceType);

        // Register the per-part handle so `m`, `m.name`, `m.id` resolve leniently in the body.
        _registry.DeclareVariable(name: expandStmt.HandleName,
            type: ComptimeHandleTypeInfo.Instance);

        bool prevInExpand = _inExpandBody;
        _inExpandBody = true;
        AnalyzeStatement(statement: expandStmt.Body);
        _inExpandBody = prevInExpand;

        _registry.ExitScope();
    }

    private void AnalyzeEachStatement(EachStatement eachStmt)
    {
        _registry.EnterScope(kind: ScopeKind.Loop, name: "each");

        // Analyze iterable expression
        TypeSymbol iterableType = AnalyzeExpression(expression: eachStmt.Iterable);

        // Get element type from iterable
        TypeSymbol elementType =
            GetIterableElementType(iterableType: iterableType, location: eachStmt.Location);

        // Handle either simple variable or destructuring pattern
        if (eachStmt.Variable != null)
        {
            // Simple variable binding: for item in items
            _registry.DeclareVariable(name: eachStmt.Variable, type: elementType,
                location: eachStmt.Location);
        }
        else if (eachStmt.VariablePattern != null)
        {
            // Destructuring pattern: for (index, item) in items.enumerate()
            DeclareEachDestructuringBindings(eachStmt: eachStmt, elementType: elementType);
        }

        // #22: Track active iteration source for reshaping-during-iteration check
        string? iterationSourceName = eachStmt.Iterable is IdentifierExpression iterSource
            ? iterSource.Name
            : null;

        if (iterationSourceName != null)
        {
            _activeIterationSources.Add(item: iterationSourceName);
        }

        // Analyze loop body
        AnalyzeStatement(statement: eachStmt.Body);

        if (iterationSourceName != null)
        {
            _activeIterationSources.Remove(item: iterationSourceName);
        }

        _registry.ExitScope();
    }

    /// <summary>
    /// Declares the bindings of an each-loop destructuring pattern: matched positionally against a
    /// tuple element type, or with error types (plus a diagnostic) for a non-tuple element type.
    /// </summary>
    private void DeclareEachDestructuringBindings(EachStatement eachStmt, TypeSymbol elementType)
    {
        if (elementType is TupleTypeInfo tupleType)
        {
            // Check arity match
            int bindingCount = eachStmt.VariablePattern!.Bindings.Count;
            if (bindingCount != tupleType.Arity)
            {
                ReportError(code: SemanticDiagnosticCode.DestructuringArityMismatch,
                    message:
                    $"Destructuring pattern has {bindingCount} bindings but tuple has {tupleType.Arity} elements.",
                    location: eachStmt.VariablePattern.Location);
            }

            // Declare each binding with its corresponding tuple element type
            for (int i = 0; i < eachStmt.VariablePattern.Bindings.Count; i++)
            {
                DestructuringBinding binding = eachStmt.VariablePattern.Bindings[index: i];
                if (binding.BindingName != null)
                {
                    TypeSymbol bindingType = i < tupleType.Arity
                        ? tupleType.ElementTypes[index: i]
                        : ErrorTypeInfo.Instance;
                    _registry.DeclareVariable(name: binding.BindingName, type: bindingType);
                }
            }
        }
        else
        {
            // Non-tuple type with destructuring pattern
            ReportError(code: SemanticDiagnosticCode.DestructuringArityMismatch,
                message:
                $"Cannot destructure non-tuple type '{elementType.Name}' in for loop.",
                location: eachStmt.VariablePattern!.Location);
            // Still declare variables with error type so analysis can continue
            foreach (DestructuringBinding binding in eachStmt.VariablePattern.Bindings)
            {
                if (binding.BindingName != null)
                {
                    _registry.DeclareVariable(name: binding.BindingName,
                        type: ErrorTypeInfo.Instance);
                }
            }
        }
    }

    private void AnalyzeWhenStatement(WhenStatement whenStmt)
    {
        TypeSymbol matchedType = AnalyzeExpression(expression: whenStmt.Expression);

        // Comptime arm-expansion (`when me` / `expand m in branchof(T)` / `is ${m.type} x => …`): the
        // concrete arms are unknown until monomorphization. Validate the template leniently — the
        // handle `m` and the payload binding type-check via deferral (ErrorTypeInfo) — plus any
        // EXPLICIT clauses written alongside it (e.g. `is None => …`). Skip the exhaustiveness/order
        // checks below, which don't apply until the arms are unrolled.
        if (whenStmt.ArmExpansion is { } armExp)
        {
            AnalyzeWhenArmExpansion(whenStmt: whenStmt, armExp: armExp);
            return;
        }

        // #161: Mark Lookup variable as dismantled when targeted by 'when'
        if (whenStmt.Expression is IdentifierExpression whenTarget)
        {
            _pendingLookupVars.RemoveAll(match: v => v.Name == whenTarget.Name);
        }

        // #88: Pattern order enforcement — else/wildcard must be last, detect unreachable patterns
        CheckWhenPatternOrder(whenStmt: whenStmt);

        // #130/#148: Duplicate pattern detection
        CheckWhenDuplicatePatterns(whenStmt: whenStmt);

        // Track handled patterns for narrowing the else clause
        bool handledNone = false;
        bool handledNoneValue = false;
        bool handledCrashable = false;

        // Suflae: `when` is for variants / carriers / values — not entity references. An entity
        // reference has only two flow states (none / present), for which `if x is None` /
        // `if x isnot None` is the idiom (and the only shape with a null-check codegen lowering).
        // Reject `when <entity-ref>` up front so it can't silently miscompile.
        if (_registry.Language == Language.Suflae && IsEntityRefType(type: matchedType))
        {
            ReportError(code: SemanticDiagnosticCode.NullableEntityDeref,
                message:
                "Cannot use 'when' on an entity reference. Match its none-state with " +
                "'if x is None' / 'if x isnot None' instead.",
                location: whenStmt.Expression.Location);
        }

        // Variant subject narrowing: when the subject is a plain-variant VARIABLE, remember which
        // arms the `is Arm` clauses cover so the else clause can exclude them and — if exactly one
        // arm remains — narrow the subject to it (usable without rebinding). Excluding through the
        // scope registry also composes with a nested `if x is …` in the else body.
        string? whenVarName = (whenStmt.Expression as IdentifierExpression)?.Name;
        VariantTypeInfo? whenVariant =
            whenVarName != null && matchedType is VariantTypeInfo wv && !IsCarrierType(type: matchedType)
                ? wv
                : null;
        var handledArms = new List<string>();

        foreach (WhenClause clause in whenStmt.Clauses)
        {
            _registry.EnterScope(kind: ScopeKind.Block, name: "when_clause");

            // Variant subject narrowing for the else clause: exclude every arm the preceding clauses
            // matched (else is always last, so the list is complete), then narrow the subject if a
            // single arm is left. Excluding through the scope registry composes with a nested
            // `if x is …` inside the else body. Runs before the body is analyzed.
            if (whenVariant != null && whenVarName != null && clause.Pattern is ElsePattern)
            {
                foreach (string armName in handledArms)
                    _registry.ExcludeVariantArm(name: whenVarName, armFullName: armName);
                IReadOnlyCollection<string> excluded =
                    _registry.GetExcludedVariantArms(name: whenVarName);
                if (whenVariant.Members.Where(predicate: m => !excluded.Contains(m.Name))
                        .ToList() is [{ Type: not null } sole])
                {
                    _registry.NarrowVariable(name: whenVarName, narrowedType: sole.Type);
                }
            }

            // Track which patterns are handled (before the else clause).
            // Maybe[T] and Lookup[T] absent state is matched by `is None`.
            // Result[T] has no absent state; only Crashable | T.
            string? carrierBase = GetCarrierBaseName(type: matchedType);
            bool carrierUsesNoneForAbsent = carrierBase is "Maybe" or "Lookup";
            if (carrierUsesNoneForAbsent && IsNonePattern(pattern: clause.Pattern))
            {
                handledNone = true;
            }
            else if (carrierBase == "Result" && IsNoneTypePattern(pattern: clause.Pattern))
            {
                handledNoneValue = true;
            }
            else if (IsCrashablePattern(pattern: clause.Pattern))
            {
                handledCrashable = true;
            }

            switch (clause.Pattern)
            {
                case ElsePattern elsePat when IsCarrierType(type: matchedType):
                {
                    // Compute narrowed type for else clause binding
                    TypeSymbol? narrowedType = ComputeNarrowedType(type: matchedType,
                        eliminateNone: handledNone,
                        eliminateNoneValue: handledNoneValue,
                        eliminateCrashable: handledCrashable);

                    if (narrowedType != null && elsePat.VariableName != null)
                    {
                        // Declare with narrowed type instead of original matchedType
                        DeclarePatternVariable(name: elsePat.VariableName,
                            type: narrowedType,
                            location: elsePat.Location);
                        AnalyzeStatement(statement: clause.Body);
                        _registry.ExitScope();
                        continue;
                    }

                    break;
                }
            }

            // Analyze pattern and bind variables
            AnalyzePattern(pattern: clause.Pattern, matchedType: matchedType);

            if (whenVariant != null && whenVarName != null)
            {
                // Record which variant arm this clause FULLY matched (unguarded, `is None` included)
                // so the trailing else clause can exclude it.
                if (ResolveVariantArm(pattern: clause.Pattern, variant: whenVariant) is
                    { } matchedArm)
                {
                    handledArms.Add(item: matchedArm.Name);
                }

                // Narrow the subject to the matched arm inside THIS arm's body, so it's usable
                // without rebinding (`is Point => me me.x`). Safe even for a guarded arm — the body
                // only runs once the arm matched — so unwrap the guard to reach the arm.
                Pattern armPattern =
                    clause.Pattern is GuardPattern gp ? gp.InnerPattern : clause.Pattern;
                if (ResolveVariantArm(pattern: armPattern, variant: whenVariant) is
                    { Type: not null } bodyArm)
                {
                    _registry.NarrowVariable(name: whenVarName, narrowedType: bodyArm.Type);
                }
            }

            // Analyze clause body
            AnalyzeStatement(statement: clause.Body);

            _registry.ExitScope();
        }

        // Check exhaustiveness for enumerable types (choice, variant, error-handling, Bool)
        if (matchedType is ChoiceTypeInfo or VariantTypeInfo || IsCarrierType(type: matchedType) ||
            IsBoolType(type: matchedType))
        {
            CheckWhenExhaustiveness(whenStmt: whenStmt, matchedType: matchedType);
        }
    }

    /// <summary>
    /// Analyzes a comptime arm-expansion `when` (`when me` / `is ${m.type} x => …`): its explicit
    /// clauses and the expansion template body are validated leniently (bindings deferred to
    /// monomorphization) with the exhaustiveness/order checks skipped.
    /// </summary>
    private void AnalyzeWhenArmExpansion(WhenStatement whenStmt, WhenArmExpansion armExp)
    {
        // The subject type is the generic param (arms unknown pre-monomorph), so the explicit
        // clauses' patterns (e.g. `is None`) can't be validated here — defer them. Declare any
        // simple type-pattern binding leniently so its body still type-checks.
        foreach (WhenClause clause in whenStmt.Clauses)
        {
            _registry.EnterScope(kind: ScopeKind.Block, name: "when-clause");
            if (clause.Pattern is TypePattern { VariableName: { } explicitBind })
            {
                _registry.DeclareVariable(name: explicitBind, type: ErrorTypeInfo.Instance);
            }

            AnalyzeStatement(statement: clause.Body);
            _registry.ExitScope();
        }

        _registry.EnterScope(kind: ScopeKind.Block, name: "expand-arm");
        _registry.DeclareVariable(name: armExp.HandleName,
            type: ComptimeHandleTypeInfo.Instance);
        if (armExp.Template.Pattern is SpliceTypePattern { VariableName: { } bindName })
        {
            _registry.DeclareVariable(name: bindName, type: ErrorTypeInfo.Instance);
        }

        AnalyzeStatement(statement: armExp.Template.Body);
        _registry.ExitScope();
    }

    /// <summary>
    /// #88: Enforces that an else/wildcard clause is last — any clause after one is unreachable.
    /// </summary>
    private void CheckWhenPatternOrder(WhenStatement whenStmt)
    {
        bool seenElse = false;
        foreach (WhenClause clause in whenStmt.Clauses)
        {
            if (seenElse)
            {
                ReportError(code: SemanticDiagnosticCode.PatternOrderViolation,
                    message: "Unreachable pattern after 'else' or wildcard.",
                    location: clause.Pattern.Location);
            }

            if (clause.Pattern is ElsePattern or WildcardPattern)
            {
                seenElse = true;
            }
        }
    }

    /// <summary>
    /// #130/#148: Reports clauses whose pattern key repeats an earlier clause's.
    /// </summary>
    private void CheckWhenDuplicatePatterns(WhenStatement whenStmt)
    {
        var seenPatterns = new HashSet<string>();
        foreach (WhenClause clause in whenStmt.Clauses)
        {
            string? patternKey = GetPatternKey(pattern: clause.Pattern);
            if (patternKey != null && !seenPatterns.Add(item: patternKey))
            {
                ReportError(code: SemanticDiagnosticCode.DuplicatePattern,
                    message: $"Duplicate pattern: {patternKey}.",
                    location: clause.Pattern.Location);
            }
        }
    }

    /// <summary>
    /// Checks exhaustiveness of a `when` over an enumerable type: a catch-all clause is always
    /// exhaustive, otherwise a missing 'Crashable' arm on Result/Lookup is a hard error and any
    /// other gap is a non-exhaustive warning.
    /// </summary>
    private void CheckWhenExhaustiveness(WhenStatement whenStmt, TypeSymbol matchedType)
    {
        bool hasCatchAll = whenStmt.Clauses.Any(predicate: c =>
            c.Pattern is WildcardPattern or ElsePattern or IdentifierPattern);

        if (hasCatchAll)
        {
            _exhaustiveWhens.Add(item: whenStmt);
            return;
        }

        ExhaustivenessResult exhaustiveness = CheckExhaustiveness(
            clauses: whenStmt.Clauses,
            matchedType: matchedType);

        if (exhaustiveness.IsExhaustive)
        {
            _exhaustiveWhens.Add(item: whenStmt);
            return;
        }

        string missing = exhaustiveness.MissingCases.Count > 0
            ? $" Missing cases: {string.Join(separator: ", ", values: exhaustiveness.MissingCases)}."
            : "";

        // #89: Result/Lookup missing Crashable catch-all is an error, not a warning
        if (IsCarrierType(type: matchedType) && !IsMaybeType(type: matchedType) &&
            exhaustiveness.MissingCases.Contains(item: "Crashable"))
        {
            ReportError(code: SemanticDiagnosticCode.NonExhaustiveMatch,
                message:
                $"Pattern match on '{matchedType.Name}' requires a 'Crashable' catch-all arm.{missing}",
                location: whenStmt.Location);
        }
        else
        {
            ReportWarning(code: SemanticWarningCode.NonExhaustiveWhen,
                message:
                $"When statement may not cover all cases of '{matchedType.Name}'.{missing}",
                location: whenStmt.Location);
        }
    }

    private void AnalyzeReturnStatement(ReturnStatement ret)
    {
        if (_currentRoutine == null)
        {
            ReportError(code: SemanticDiagnosticCode.ReturnOutsideFunction,
                message: "Return statement outside of function.",
                location: ret.Location);
            return;
        }

        if (ret.Value != null)
        {
            // Bare `return` is normalized by NoneReturnNormalizationPass to `return None`
            // with Location == ret.Location. Treat these as unreachable-path terminators:
            // skip the type check so exhaustive when-else blocks can end with bare `return`.
            bool isNormalizedBareReturn =
                ret.Value is IdentifierExpression { Name: "None" } blankId &&
                blankId.Location == ret.Location;

            // Pass expected return type for contextual literal inference
            TypeSymbol returnType = AnalyzeExpression(expression: ret.Value,
                expectedType: _currentRoutine.ReturnType);

            // Validate that tokens cannot be returned (RazorForge only)
            ValidateNotTokenReturnType(type: returnType, location: ret.Location);

            if (!isNormalizedBareReturn &&
                _currentRoutine.ReturnType != null && !IsAssignableTo(source: returnType,
                    target: _currentRoutine.ReturnType))
            {
                ReportError(code: SemanticDiagnosticCode.ReturnTypeMismatch,
                    message:
                    $"Cannot return value of type '{returnType.Name}' from function expecting '{_currentRoutine.ReturnType.Name}'.",
                    location: ret.Location);
            }
        }
    }

    /// <summary>
    /// Analyzes a becomes statement (block result value).
    /// Becomes is used in multi-statement when/if branches to explicitly indicate the branch's result.
    /// </summary>
    private void AnalyzeBecomesStatement(BecomesStatement becomesStmt)
    {
        // Analyze the becomes expression
        // For now, we just validate the expression type - context validation
        // (checking that becomes appears in an appropriate block context) can be
        // added in a future phase when we track block expression contexts
        TypeSymbol becomesType = AnalyzeExpression(expression: becomesStmt.Value);

        // Validate that tokens cannot be block results (RazorForge only)
        ValidateNotTokenReturnType(type: becomesType, location: becomesStmt.Location);
    }

    private void AnalyzeThrowStatement(ThrowStatement throwStmt)
    {
        if (_currentRoutine == null)
        {
            ReportWarning(code: SemanticWarningCode.ThrowAbsentInNonFailable,
                message: "Throw statement outside any routine.",
                location: throwStmt.Location);
            return;
        }

        // Failability is now INFERRED: a recoverable `throw` in a routine not declared `!` is FINE —
        // it simply makes the routine inferred-failable (the `InferFailableRoutines` fixpoint sets
        // IsFailable=true before codegen). The declaration `!` is an OPTIONAL honest annotation, no
        // longer required at the throw site. `pierce` (IsFatal) stays a fatal uncatchable crash that
        // never marks the routine failable.
        TypeSymbol errorType = AnalyzeExpression(expression: throwStmt.Error);

        // Only `crashable`-kind types are throwable errors. The `crashable` keyword implicitly
        // confers the Crashable contract; no other type kind may obey it (enforced at the
        // declaration site — see ValidateTypeProtocolImplementation), so there is no longer an
        // explicit-`obeys Crashable` path for records/entities. `Error`/ErrorTypeInfo are the
        // catch-all error references used by generic error handling.
        bool isCrashable = errorType.Category == TypeCategory.Crashable ||
                           errorType is ErrorTypeInfo ||
                           errorType.Name == "Error";
        if (!isCrashable)
        {
            ReportError(code: SemanticDiagnosticCode.ThrowNotCrashable,
                message:
                $"Only `crashable`-kind types can be thrown, got '{errorType.Name}' ({errorType.Category}). " +
                "Declare the error type with the `crashable` keyword.",
                location: throwStmt.Error.Location);
        }

        // Mark routine as having throw statements (drives both failability inference and variant
        // generation). Recorded UNCONDITIONALLY for a recoverable `throw` — the inference fixpoint reads
        // HasThrow to derive IsFailable, so it must be set even when `!` was not declared. A `pierce`
        // never marks failable — it is a crash, not a recoverable failure.
        if (!throwStmt.IsFatal)
            _currentRoutine.HasThrow = true;
    }

    private void AnalyzeAbsentStatement(AbsentStatement absent)
    {
        if (_currentRoutine == null)
        {
            ReportWarning(code: SemanticWarningCode.ThrowAbsentInNonFailable,
                message: "Absent statement outside any routine.",
                location: absent.Location);
            return;
        }

        // Failability is INFERRED: an `absent` in a routine not declared `!` is FINE and simply makes
        // the routine inferred-failable. Mark routine as having absent statements (for both inference
        // and variant generation) UNCONDITIONALLY — the fixpoint reads HasAbsent to derive IsFailable.
        _currentRoutine.HasAbsent = true;
    }

    private void AnalyzeBreakStatement(BreakStatement breakStmt)
    {
        if (!_registry.CurrentScope.IsInLoop)
        {
            ReportError(code: SemanticDiagnosticCode.BreakOutsideLoop,
                message: "Break statement is only allowed inside a loop.",
                location: breakStmt.Location);
        }
    }

    private void AnalyzeContinueStatement(ContinueStatement continueStmt)
    {
        if (!_registry.CurrentScope.IsInLoop)
        {
            ReportError(code: SemanticDiagnosticCode.ContinueOutsideLoop,
                message: "Continue statement is only allowed inside a loop.",
                location: continueStmt.Location);
        }
    }

    private void AnalyzeDestructuringStatement(DestructuringStatement destruct)
    {
        TypeSymbol initType = AnalyzeExpression(expression: destruct.Initializer);

        // Analyze the destructuring pattern and bind variables
        AnalyzeDestructuringPattern(pattern: destruct.Pattern, sourceType: initType);
    }

    /// <summary>
    /// Analyzes a discard statement (explicitly ignores a return value).
    /// Used to explicitly indicate that a routine's return value is intentionally ignored.
    /// </summary>
    private void AnalyzeDiscardStatement(DiscardStatement discard)
    {
        // discard must target a routine call, not an arbitrary expression like a literal or variable.
        // Explicit-generic calls (`f[T](...)`) parse to GenericMemberRoutineCallExpression — also a call.
        if (discard.Expression is not (CallExpression or GenericMemberRoutineCallExpression))
        {
            ReportError(code: SemanticDiagnosticCode.InvalidDiscardTarget,
                message: "'discard' can only be used with routine calls. " +
                         "Use 'discard some_routine()' to explicitly ignore a return value.",
                location: discard.Location);
        }

        // Analyze the expression - this validates the expression and checks for errors
        // The result is intentionally discarded
        TypeSymbol discardedType = AnalyzeExpression(expression: discard.Expression);

        // `discard foo()` on an Agent is the lazy-async footgun: `discard` only throws away the value,
        // it does NOT run the routine — an un-launched Agent's body never executes. (In the old eager
        // model `discard foo()` still ran the work.) So warn even though the value was explicitly ignored.
        if (discardedType is RecordTypeInfo dag && (dag.GenericDefinition?.Name ?? dag.Name) == "Agent")
        {
            string routineName = discard.Expression switch
            {
                CallExpression { Callee: IdentifierExpression id } => id.Name,
                CallExpression { Callee: MemberExpression m } => m.MemberName,
                _ => "routine"
            };
            ReportWarning(code: SemanticWarningCode.AsyncAgentNeverLaunched,
                message: $"`discard {routineName}()` does NOT run the routine — `discard` only ignores " +
                         "the value, and an un-launched Agent never executes. Call `.execute()` to run " +
                         "it in the background, or `.retrieve()` to run it and await the value.",
                location: discard.Location);
        }
    }

    private void AnalyzeDangerStatement(DangerStatement danger)
    {
        if (_registry.Language == Language.Suflae)
        {
            ReportError(code: SemanticDiagnosticCode.FeatureNotInSuflae,
                message: "Danger blocks are not available in Suflae.",
                location: danger.Location);
            return;
        }

        // Danger blocks cannot be nested
        if (InDangerBlock)
        {
            ReportError(code: SemanticDiagnosticCode.NestedDangerBlock,
                message: "Danger blocks cannot be nested.",
                location: danger.Location);
            return;
        }

        // Enter danger scope
        _registry.EnterScope(kind: ScopeKind.Block, name: "danger");
        _dangerBlockDepth = 1;

        try
        {
            AnalyzeBlockStatement(block: danger.Body);
        }
        finally
        {
            _dangerBlockDepth = 0;
            _registry.ExitScope();
        }
    }

    private void AnalyzeUsingStatement(UsingStatement usingStmt)
    {
        // Mark the resource node so a multi-threaded access token (Consulting/Amending) produced
        // directly here is accepted; the same token produced anywhere else is rejected (RF-S629),
        // keeping its lock strictly `using`-scoped. Save/restore to support nested `using`.
        ISyntaxTreeNode? previousUsingResource = _usingResourceNode;
        _usingResourceNode = usingStmt.Resource;
        // Analyze the resource expression to get its type
        TypeSymbol resourceType = AnalyzeExpression(expression: usingStmt.Resource);
        _usingResourceNode = previousUsingResource;

        // Readers-XOR-writer (RF-S630): if this `using` opens an MT access token on a named Guarded
        // handle, check it against the holds already live in the enclosing `using` scopes on the SAME
        // handle. A writer (`amend`) conflicts with any other hold; readers (`consult`) coexist.
        // The hold is pushed for the duration of the body and popped on exit, so only OVERLAPPING
        // scopes conflict (sequential `using`s on the same handle are fine).
        string accessBase = resourceType.BareName;
        bool opensAccessToken = accessBase is Compiler.Declaration.RuntimeContract.Consulting or Compiler.Declaration.RuntimeContract.Amending;
        string? accessHandle = opensAccessToken
            ? ExtractAccessReceiverName(resource: usingStmt.Resource)
            : null;
        if (accessHandle != null)
        {
            CheckReadersXorWriter(usingStmt: usingStmt, accessBase: accessBase,
                accessHandle: accessHandle);
        }

        // The bound variable type defaults to the resource type, but may be overridden
        // by enter's return type when it returns non-void.
        TypeSymbol boundType = resourceType;

        // A `using` target must obey `Enterable` — the protocol that declares the `enter`/`exit`
        // scope-management contract. Conformance (not just the presence of `enter`/`exit` by name)
        // is the gate, so being `using`-able is an explicit, checked capability.
        if (_registry.Language == Language.RazorForge)
        {
            ValidateEnterableResource(usingStmt: usingStmt, resourceType: resourceType,
                boundType: ref boundType);
        }

        // Create a new scope for the using block
        _registry.EnterScope(kind: ScopeKind.Block, name: "using");

        // Declare the binding variable in the using scope
        _registry.DeclareVariable(name: usingStmt.Name, type: boundType,
            location: usingStmt.Location);

        // Analyze the body
        AnalyzeStatement(statement: usingStmt.Body);

        // #171/#172: Token/resource scope escape — validate that the using-bound variable
        // is not returned or stored in outer scope (handled by ValidateNotTokenReturnType
        // for tokens, and conceptually enforced by scope exit for resources)

        _registry.ExitScope();

        // Pop the MT access hold now that the scope has closed (readers-XOR-writer, RF-S630).
        if (accessHandle != null)
            _activeAccessHolds.RemoveAt(index: _activeAccessHolds.Count - 1);

        // The `fallback` branch runs when acquisition fails: no hold is taken and the bound
        // name is NOT in scope. Analyze it in its own fresh scope, outside the access hold.
        if (usingStmt.FallbackBody != null)
        {
            _registry.EnterScope(kind: ScopeKind.Block, name: "using-fallback");
            AnalyzeStatement(statement: usingStmt.FallbackBody);
            _registry.ExitScope();
        }
    }

    /// <summary>
    /// RF-S630 readers-XOR-writer: reports a conflict when the access token this `using` opens
    /// overlaps an active hold in an enclosing scope (a writer excludes all; readers coexist), then
    /// pushes this hold onto the active-holds stack for the duration of the body.
    /// </summary>
    private void CheckReadersXorWriter(UsingStatement usingStmt, string accessBase,
        string accessHandle)
    {
        bool isWriter = accessBase == Compiler.Declaration.RuntimeContract.Amending;
        int accessIdentity = GetOrAssignHandleIdentity(path: accessHandle);
        foreach ((string Handle, int Identity, bool IsWriter, SourceLocation Location) hold
                 in _activeAccessHolds)
        {
            // Two holds touch the same memory when they resolve to the same controller identity
            // (aliased handles — `s` and `s2 = s.share()`) OR when their syntactic paths overlap
            // on a field boundary (a parent handle and one of its sub-handles).
            bool sameMemory = hold.Identity == accessIdentity ||
                              PathsOverlap(a: hold.Handle, b: accessHandle);
            if (!sameMemory || (!isWriter && !hold.IsWriter))
                continue;
            string newKind = isWriter ? "amend()" : "consult()";
            string heldKind = hold.IsWriter ? "amend()" : "consult()";
            string overlapNote = hold.Handle == accessHandle
                ? "the same shared handle"
                : hold.Identity == accessIdentity
                    ? $"the aliased handle '{hold.Handle}' (same shared data)"
                    : $"the overlapping handle '{hold.Handle}'";
            ReportError(code: SemanticDiagnosticCode.ReadersXorWriter,
                message:
                $"'{newKind}' on '{accessHandle}' conflicts with an active '{heldKind}' on " +
                $"{overlapNote} in an enclosing 'using' scope. A writer ('amend') excludes all other " +
                "access; readers ('consult') may coexist only with other readers.",
                location: usingStmt.Location);
            break;
        }

        _activeAccessHolds.Add(
            item: (accessHandle, accessIdentity, isWriter, usingStmt.Location));
    }

    /// <summary>
    /// Validates a RazorForge `using` resource obeys `Enterable`; when it does, overrides the bound
    /// variable type with `enter`'s non-void return and requires `try_enter` for a `fallback` branch.
    /// </summary>
    private void ValidateEnterableResource(UsingStatement usingStmt, TypeSymbol resourceType,
        ref TypeSymbol boundType)
    {
        if (!ImplementsProtocol(type: resourceType, protocolName: "Enterable"))
        {
            ReportError(code: SemanticDiagnosticCode.UsingTargetMissingEnterExit,
                message:
                $"Using target of type '{resourceType.Name}' must obey 'Enterable' (which provides " +
                "'enter'/'exit') for scope-managed resource access.",
                location: usingStmt.Location);
            return;
        }

        // The bound variable's type is `enter`'s return type when non-void (pass-through).
        // LookupMemberRoutine handles generic fallback (Viewing[Point].enter -> Viewing.enter).
        RoutineInfo? enterMemberRoutine =
            _registry.LookupMemberRoutine(type: resourceType, memberRoutineName: "enter");
        if (enterMemberRoutine?.ReturnType is { IsNone: false } enterReturn)
            boundType = enterReturn;

        // A `fallback` branch drives a non-blocking acquisition — the resource must
        // provide `try_enter` (returns Bool: did the hold succeed?). Types whose entry
        // can only block (no `try_enter`) cannot take a `fallback`.
        if (usingStmt.FallbackBody != null &&
            _registry.LookupMemberRoutine(type: resourceType, memberRoutineName: "try_enter") == null)
        {
            ReportError(code: SemanticDiagnosticCode.UsingFallbackRequiresTryEnter,
                message:
                $"'using ... fallback' requires the resource type '{resourceType.Name}' to " +
                "provide 'try_enter' (a non-blocking acquisition). This type only supports " +
                "blocking entry — drop the 'fallback' branch.",
                location: usingStmt.Location);
        }
    }

    /// <summary>
    /// Extracts a path key for the Guarded handle of an `consult`/`amend` access expression — the
    /// receiver of `s.consult()` / `s.amend()`, as a dotted path so distinct fields are distinct
    /// handles: `s` → "s", `s.a` → "s.a". This makes `s.a.amend()` and `s.b.amend()` independent
    /// (both amendable in one scope) while `s.a` amended twice still conflicts. Returns null for
    /// receivers that aren't a pure identifier/field path (indexing, call results), which the
    /// readers-XOR-writer check conservatively skips (no false positives).
    /// </summary>
    private static string? ExtractAccessReceiverName(Expression resource)
    {
        return resource is CallExpression { Callee: MemberExpression { Object: var receiver } }
            ? BuildAccessPath(expr: receiver)
            : null;
    }

    /// <summary>Whether two access paths overlap — equal, or one a prefix of the other on a field
    /// boundary (`s.a` overlaps `s.a.x` and `s`, but not `s.b` or `s.ab`). Overlapping paths name
    /// memory where one contains the other, so concurrent access to them conflicts.</summary>
    private static bool PathsOverlap(string a, string b)
    {
        return a == b || a.StartsWith(value: b + ".") || b.StartsWith(value: a + ".");
    }

    /// <summary>Builds a dotted path for an identifier/field-access chain (`s.a.b` → "s.a.b"), or
    /// null if the chain bottoms out on anything else (indexing, a call, a literal).</summary>
    private static string? BuildAccessPath(Expression expr)
    {
        return expr switch
        {
            IdentifierExpression id => id.Name,
            MemberExpression { Object: var inner, MemberName: var prop } =>
                BuildAccessPath(expr: inner) is { } prefix ? $"{prefix}.{prop}" : null,
            _ => null
        };
    }

    /// <summary>Returns the controller identity for a Guarded/Witnessed handle path. A path bound to a
    /// tracked handle (recorded at its <c>var</c> declaration, see
    /// <see cref="RecordSharedHandleIdentity"/>) reuses its identity; an untracked path is assigned a
    /// fresh unique identity on first use and remembered, so repeated uses of the same path match
    /// (the readers-XOR-writer check then degrades to per-path keying — the pre-aliasing behaviour —
    /// for handles whose origin it can't see).</summary>
    private int GetOrAssignHandleIdentity(string path)
    {
        if (_sharedHandleIdentity.TryGetValue(key: path, out int id))
            return id;
        id = _nextSharedHandleIdentity++;
        _sharedHandleIdentity[key: path] = id;
        return id;
    }

    /// <summary>Records the controller identity of a freshly declared Guarded/Witnessed handle from its
    /// initializer, so later aliases and access-token receivers resolve to the same controller:
    /// <list type="bullet">
    /// <item>a clone (<c>s.share()</c> RC copy / <c>s.observe()</c> strong→weak) or a plain copy
    /// (<c>var s2 = s</c>) INHERITS the source handle's identity;</item>
    /// <item>anything else — including a fresh Arc construction <c>Guarded[T, P](from: n)</c> — gets a
    /// fresh identity (conservative — a missed alias only weakens the check, never a false positive).</item>
    /// </list></summary>
    private void RecordSharedHandleIdentity(string name, Expression? initializer)
    {
        int identity = initializer switch
        {
            // Clone: `s.share()` (RC copy verb — mint a co-owner) / `s.observe()` (strong→weak conversion)
            // — inherit the receiver handle's identity (both alias the same controller).
            CallExpression
                {
                    Callee: MemberExpression
                    {
                        Object: var receiver,
                        MemberName: Compiler.Declaration.RuntimeContract.RefCount.Share or "observe"
                    }
                } when BuildAccessPath(expr: receiver) is { } recvPath =>
                GetOrAssignHandleIdentity(path: recvPath),
            // Plain copy: `var s2 = s` — inherit (usually blocked by the copy-verb rule, handled
            // here for completeness).
            IdentifierExpression copySource =>
                GetOrAssignHandleIdentity(path: copySource.Name),
            _ => _nextSharedHandleIdentity++
        };
        _sharedHandleIdentity[key: name] = identity;
    }
}
