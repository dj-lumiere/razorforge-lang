using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Compiler.Verification;

using TypeSymbol = TypeInfo;

public sealed partial class SemanticVerifier
{
    private record NarrowingInfo(
        string VariableName,
        TypeSymbol? ThenBranchType,
        TypeSymbol? ElseBranchType)
    {
        /// <summary>Suflae flow typing: the then-branch proves <see cref="VariableName"/> non-none
        /// (e.g. `if x isnot none`). Independent of type narrowing — nullable/non-null share a type.</summary>
        public bool ThenNonNull { get; init; }

        /// <summary>Suflae flow typing: the else-branch (and guard-clause continuation) proves
        /// <see cref="VariableName"/> non-none (e.g. `if x is none: return`).</summary>
        public bool ElseNonNull { get; init; }
    }

    /// <summary>A general-variant `x is A` / `x isnot A` test on a variant variable — the seed for
    /// arm-exclusion narrowing (`if x is A {} elseif x is B {} else { /* x is C */ }`).</summary>
    private sealed record VariantIsNarrowing(
        string VarName, VariantTypeInfo Variant, VariantMemberInfo Arm, bool Negated);

    /// <summary>
    /// Recognizes a `variable is Arm` / `variable isnot Arm` condition where <c>variable</c> has a
    /// user variant type and <c>Arm</c> is one of its arms. Returns null otherwise (carriers, None,
    /// non-variant subjects, non-identifier subjects are handled elsewhere / not narrowed here).
    /// </summary>
    private VariantIsNarrowing? TryGetVariantIsNarrowing(Expression condition)
    {
        if (condition is not IsPatternExpression
            {
                Expression: IdentifierExpression id, Pattern: TypePattern tp
            } isPat)
        {
            return null;
        }

        // Use the CURRENT type — the already-narrowed type if the variable was narrowed by an
        // enclosing check, else its declared type. This lets nested narrowing compose: after
        // `if o is None` narrows `o` (Outer) to its sole remaining arm `Inner`, a further
        // `if o is S32` matches against `Inner`'s arms. Skip carriers (own narrowing path).
        TypeInfo? subjectType = _registry.GetNarrowedType(name: id.Name)
            ?? _registry.LookupVariable(name: id.Name)?.Type;
        if (subjectType is not VariantTypeInfo variant || IsCarrierType(type: variant))
        {
            return null;
        }

        VariantMemberInfo? arm = ResolveVariantArm(pattern: isPat.Pattern, variant: variant);
        return arm == null
            ? null
            : new VariantIsNarrowing(VarName: id.Name, Variant: variant, Arm: arm,
                Negated: isPat.IsNegated);
    }

    /// <summary>
    /// Resolves the variant arm a pattern FULLY matches (`is Arm`, `is Arm x`, `is Arm (a, b)`, or
    /// `is None`) to its <see cref="VariantMemberInfo"/> — by RESOLVED TYPE identity via
    /// <see cref="VariantTypeInfo.FindMember"/>, NOT by parsing the type name — or null when the
    /// pattern is not a single-arm match. A <see cref="GuardPattern"/> returns null on purpose: a
    /// guarded arm does not fully cover its arm (the guard may be false), so it must not exclude it.
    /// </summary>
    private VariantMemberInfo? ResolveVariantArm(Pattern pattern, VariantTypeInfo variant)
    {
        // `is None` matches the payload-less None arm.
        if (IsNonePattern(pattern: pattern))
            return variant.Members.FirstOrDefault(predicate: m => m.IsNone);

        TypeExpression? armExpr = pattern switch
        {
            TypePattern tp => tp.Type,
            TypeDestructuringPattern td => td.Type,
            _ => null // GuardPattern / ElsePattern / comparison / literal → not a full arm match
        };
        if (armExpr == null) return null;

        TypeInfo? armType = armExpr.ResolvedType ?? _registry.LookupType(name: armExpr.Name);
        return armType == null ? null : variant.FindMember(type: armType);
    }

    /// <summary>
    /// Applies variant arm narrowing to the CURRENT scope for one branch of an `if`.
    /// <paramref name="conditionTrue"/> is true in the then-branch (condition holds) and false in the
    /// else-branch. When the arm is proven present, narrows the variable to the arm's payload type;
    /// when proven absent, excludes the arm and — if exactly one arm now remains — narrows to it.
    /// None arms carry no payload, so narrowing to None is skipped (nothing to extract).
    /// </summary>
    private void ApplyVariantNarrowing(VariantIsNarrowing vn, bool conditionTrue)
    {
        bool armPresent = conditionTrue ? !vn.Negated : vn.Negated;

        if (armPresent)
        {
            if (vn.Arm.Type != null)
                _registry.NarrowVariable(name: vn.VarName, narrowedType: vn.Arm.Type);
            return;
        }

        _registry.ExcludeVariantArm(name: vn.VarName, armFullName: vn.Arm.Name);
        IReadOnlyCollection<string> excluded = _registry.GetExcludedVariantArms(name: vn.VarName);
        List<VariantMemberInfo> remaining = vn.Variant.Members
            .Where(predicate: m => !excluded.Contains(m.Name))
            .ToList();
        if (remaining is [{ Type: not null } sole])
            _registry.NarrowVariable(name: vn.VarName, narrowedType: sole.Type);
    }

    /// <summary>
    /// Attempts to extract type narrowing information from a condition expression.
    /// Handles patterns like "x is None", "x isnot None", "Not(x is None)".
    /// </summary>
    private NarrowingInfo? TryExtractNarrowingFromCondition(Expression condition)
    {
        // Handle: x is None / x is Crashable / x isnot None / x isnot Crashable
        if (condition is IsPatternExpression isPat)
        {
            return ExtractFromIsPattern(isPat: isPat);
        }

        // Handle desugared unless: Not(x is None) -> if Not(condition) { ... }
        if (condition is UnaryExpression
            {
                Operator: UnaryOperator.Not, Operand: IsPatternExpression innerIsPat
            })
        {
            // Negating the condition swaps then/else narrowing
            NarrowingInfo? inner = ExtractFromIsPattern(isPat: innerIsPat);
            if (inner == null)
            {
                return null;
            }

            // Negating the condition swaps then/else narrowing (type + nullability facts)
            return new NarrowingInfo(VariableName: inner.VariableName,
                ThenBranchType: inner.ElseBranchType,
                ElseBranchType: inner.ThenBranchType)
            {
                ThenNonNull = inner.ElseNonNull,
                ElseNonNull = inner.ThenNonNull
            };
        }

        return null;
    }

    /// <summary>
    /// Extracts narrowing info from an IsPatternExpression.
    /// </summary>
    private NarrowingInfo? ExtractFromIsPattern(IsPatternExpression isPat)
    {
        // The expression must be a simple identifier
        if (isPat.Expression is not IdentifierExpression id)
        {
            return null;
        }

        // Look up the variable to get its current type
        VariableInfo? varInfo = _registry.LookupVariable(name: id.Name);
        if (varInfo == null)
        {
            return null;
        }

        // Suflae flow typing: `x is none` / `x isnot none` on a NULLABLE entity reference proves
        // non-none-ness (not a type change — nullable and non-null share the Roamed[E] type). This
        // must be checked before the carrier logic below (which returns null for a bare Roamed).
        if (varInfo is { IsNullable: true } && IsNonePattern(pattern: isPat.Pattern))
        {
            return isPat.IsNegated
                // `x isnot none` -> then-branch proves non-none
                ? new NarrowingInfo(VariableName: id.Name, ThenBranchType: null, ElseBranchType: null)
                    { ThenNonNull = true }
                // `x is none` -> else-branch (and guard continuation) proves non-none
                : new NarrowingInfo(VariableName: id.Name, ThenBranchType: null, ElseBranchType: null)
                    { ElseNonNull = true };
        }

        // Check for existing narrowing
        TypeSymbol varType = _registry.GetNarrowedType(name: id.Name) ?? varInfo.Type;

        string? carrierBase = GetCarrierBaseName(type: varType);
        bool carrierUsesNoneForAbsent = carrierBase is "Maybe" or "Lookup";
        bool eliminateNone = carrierUsesNoneForAbsent && IsNonePattern(pattern: isPat.Pattern);
        bool eliminateNoneValue = carrierBase == "Result" && IsNoneTypePattern(pattern: isPat.Pattern);
        bool eliminateCrashable = IsCrashablePattern(pattern: isPat.Pattern);

        if (!eliminateNone && !eliminateNoneValue && !eliminateCrashable)
        {
            return null;
        }

        TypeSymbol? narrowedType = ComputeNarrowedType(type: varType,
            eliminateNone: eliminateNone,
            eliminateNoneValue: eliminateNoneValue,
            eliminateCrashable: eliminateCrashable);

        if (narrowedType == null)
        {
            return null;
        }

        if (isPat.IsNegated)
        {
            // "x isnot None" -> then branch gets the narrowed type
            return new NarrowingInfo(VariableName: id.Name,
                ThenBranchType: narrowedType,
                ElseBranchType: null);
        }

        // "x is None" -> else branch gets the narrowed type
        return new NarrowingInfo(VariableName: id.Name,
            ThenBranchType: null,
            ElseBranchType: narrowedType);
    }

    /// <summary>
    /// Checks if a pattern represents a Crashable check.
    /// The parser creates TypePattern(type: "Crashable") rather than CrashablePattern.
    /// </summary>
    private static bool IsCrashablePattern(Pattern pattern)
    {
        return pattern is CrashablePattern or TypePattern { Type.Name: "Crashable" };
    }

    /// <summary>
    /// Checks if a pattern is a generic Crashable catch-all (not a specific error type).
    /// 'is Crashable e' is a catch-all; 'is FileNotFoundError e' is not.
    /// </summary>
    private static bool IsCrashableCatchAll(Pattern pattern)
    {
        return pattern is CrashablePattern { ErrorType: null }
            or TypePattern { Type.Name: "Crashable" };
    }

    /// <summary>
    /// Computes the narrowed type after eliminating None and/or Crashable possibilities.
    /// </summary>
    /// <returns>The narrowed type, or null if narrowing is not possible.</returns>
    private static TypeSymbol? ComputeNarrowedType(TypeSymbol type, bool eliminateNone,
        bool eliminateNoneValue,
        bool eliminateCrashable)
    {
        string? baseName = GetCarrierBaseName(type: type);
        if (baseName == null || type.TypeArguments is not { Count: > 0 })
        {
            return null;
        }

        TypeSymbol valueType = type.TypeArguments[index: 0];

        return baseName switch
        {
            // Maybe<T>: eliminate None -> T
            "Maybe" when eliminateNone => valueType,

            // Result<T>: eliminate Crashable -> T
            "Result" when eliminateCrashable => valueType,

            // Lookup<T>: must eliminate both absent (None) and Crashable -> T
            "Lookup" when eliminateNone && eliminateCrashable => valueType,

            // Partial elimination on Lookup is not sufficient
            _ => null
        };
    }

    /// <summary>
    /// Checks if a statement always produces a return value (return, throw, absent, becomes).
    /// Used for missing-return validation (#144).
    /// Unlike <see cref="HasDefiniteExit"/>, this does not count break/continue as terminating,
    /// since they exit loops but don't return a value from the routine.
    /// </summary>
    private bool StatementAlwaysTerminates(Statement statement)
    {
        return statement switch
        {
            ReturnStatement => true,
            ThrowStatement => true,
            AbsentStatement => true,
            BecomesStatement => true,
            BlockStatement block => block.Statements.Any(predicate: s =>
                                        StatementAlwaysTerminates(statement: s)),
            IfStatement { ElseStatement: not null } ifStmt =>
                StatementAlwaysTerminates(statement: ifStmt.ThenStatement) &&
                StatementAlwaysTerminates(statement: ifStmt.ElseStatement),
            // A comptime arm-expansion `when` is provably exhaustive: `expand … branchof(T)` covers
            // every payload arm and any explicit clauses (e.g. `is None =>`) cover the rest. It
            // terminates iff every explicit clause body AND the arm template body terminate.
            WhenStatement { ArmExpansion: { } armExp } armWhen =>
                armWhen.Clauses.All(predicate: c =>
                    StatementAlwaysTerminates(statement: c.Body)) &&
                StatementAlwaysTerminates(statement: armExp.Template.Body),

            WhenStatement whenStmt => whenStmt.Clauses.Count > 0 &&
                                      (_exhaustiveWhens.Contains(item: whenStmt) ||
                                       whenStmt.Clauses.Any(predicate: c =>
                                           c.Pattern is ElsePattern or WildcardPattern)) &&
                                      whenStmt.Clauses.All(predicate: c =>
                                          StatementAlwaysTerminates(statement: c.Body)),
            DangerStatement danger => StatementAlwaysTerminates(statement: danger.Body),
            // An unconditional `loop` has a fall-through edge ONLY through a `break` that targets it.
            // With no such break, control leaves the loop only via `return`/`throw`/`absent`, so a
            // routine whose control reaches the loop always terminates through it. This does NOT prove
            // the loop halts (undecidable, deliberately not attempted) — it observes the absence of a
            // normal-exit edge, which IS decidable. `while` is excluded: its condition may be false on
            // entry, so it can fall through without ever running the body.
            LoopStatement loopStmt => !LoopBodyCanBreakOut(statement: loopStmt.Body),
            _ => false
        };
    }

    /// <summary>
    /// True when <paramref name="statement"/> contains a <c>break</c> that targets the *enclosing*
    /// loop — i.e. a break not nested inside another loop. Used by
    /// <see cref="StatementAlwaysTerminates"/> to decide whether an unconditional <c>loop</c> has a
    /// fall-through edge. Nested loops (<c>loop</c>/<c>while</c>/<c>each</c>) are boundaries: a break
    /// inside them belongs to that inner loop, so we do not descend into them. RazorForge breaks are
    /// unlabeled, so a break always targets the nearest enclosing loop.
    /// </summary>
    private bool LoopBodyCanBreakOut(Statement statement)
    {
        return statement switch
        {
            BreakStatement => true,
            BlockStatement block => block.Statements.Any(predicate: LoopBodyCanBreakOut),
            IfStatement ifStmt =>
                LoopBodyCanBreakOut(statement: ifStmt.ThenStatement) ||
                (ifStmt.ElseStatement is not null &&
                 LoopBodyCanBreakOut(statement: ifStmt.ElseStatement)),
            WhenStatement whenStmt =>
                whenStmt.Clauses.Any(predicate: c => LoopBodyCanBreakOut(statement: c.Body)),
            DangerStatement danger => LoopBodyCanBreakOut(statement: danger.Body),
            // Nested loops swallow their own breaks — do not descend.
            LoopStatement or WhileStatement or EachStatement => false,
            _ => false
        };
    }

    /// <summary>
    /// Checks if a statement always exits the current scope (return, throw, absent, break, continue).
    /// Used for guard clause narrowing.
    /// </summary>
    private static bool HasDefiniteExit(Statement statement)
    {
        return statement switch
        {
            ReturnStatement => true,
            ThrowStatement => true,
            AbsentStatement => true,
            BreakStatement => true,
            ContinueStatement => true,
            BlockStatement block => block.Statements.Any(predicate: s =>
                                        HasDefiniteExit(statement: s)),
            IfStatement { ElseStatement: not null } ifStmt =>
                HasDefiniteExit(statement: ifStmt.ThenStatement) &&
                HasDefiniteExit(statement: ifStmt.ElseStatement),
            _ => false
        };
    }

    /// <summary>
    /// Fixed-width numeric type names (excludes system-dependent SAddr/UAddr).
    /// </summary>
    private static readonly HashSet<string> FixedWidthNumericTypeNames =
    [
        "S8", "S16", "S32", "S64", "S128",
        "U8", "U16", "U32", "U64", "U128",
        "F16", "F32", "F64", "F128",
        "D32", "D64", "D128"
    ];

    /// <summary>
    /// Returns true if the type is a fixed-width numeric type (excludes SAddr/UAddr).
    /// </summary>
    private static bool IsFixedWidthNumericType(TypeInfo type)
    {
        return FixedWidthNumericTypeNames.Contains(item: type.Name);
    }
}
