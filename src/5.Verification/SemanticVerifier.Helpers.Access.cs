using Compiler.Diagnostics;
using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Compiler.Verification;

using TypeSymbol = TypeInfo;

public sealed partial class SemanticVerifier
{
    private static bool IsInlineOnlyTokenType(TypeSymbol type)
    {
        string baseName = type.BareName;
        return InlineOnlyTokenTypes.Contains(value: baseName);
    }

    /// <summary>
    /// Gets the token kind for display in error messages.
    /// </summary>
    private static string GetTokenKindDescription(TypeSymbol type)
    {
        string baseName = type.BareName;
        return baseName switch
        {
            Declaration.RuntimeContract.Viewing => "read-only token (Viewing)",
            Declaration.RuntimeContract.Modifying => "exclusive write token (Modifying)",
            Declaration.RuntimeContract.Consulting => "shared read token (Consulting)",
            Declaration.RuntimeContract.Amending => "exclusive shared write token (Amending)",
            _ => "token"
        };
    }

    /// <summary>
    /// Validates that a type is not an inline-only token when used as a return type.
    /// </summary>
    private void ValidateNotTokenReturnType(TypeSymbol type, SourceLocation location)
    {
        if (_registry.Language != Language.RazorForge)
        {
            return; // Token validation only applies to RazorForge
        }

        // Exempt the canonical token constructors, which legitimately return a token (e.g.
        // `T.view() -> Viewing[T]`, `T.modify() -> Modifying[T]`). These live in the stdlib
        // wrapper modules, so scope the exemption to stdlib files — a USER routine declaring a
        // token return type is escaping a scope-bound token and must be rejected.
        if (IsStdlibFile(filePath: _currentFilePath) && _currentRoutine?.ReturnType != null &&
            IsInlineOnlyTokenType(type: _currentRoutine.ReturnType))
        {
            return;
        }

        if (IsInlineOnlyTokenType(type: type))
        {
            ReportError(code: SemanticDiagnosticCode.TokenReturnNotAllowed,
                message:
                $"Cannot return {GetTokenKindDescription(type: type)} from a routine. Tokens are inline-only and cannot escape their scope.",
                location: location);
        }
    }

    /// <summary>
    /// Validates that a type is not an inline-only token when used as a member variable type.
    /// </summary>
    private void ValidateNotTokenMemberVariableType(TypeSymbol type, string memberVariableName,
        SourceLocation location)
    {
        if (_registry.Language != Language.RazorForge)
        {
            return; // Token validation only applies to RazorForge
        }

        if (IsInlineOnlyTokenType(type: type))
        {
            ReportError(code: SemanticDiagnosticCode.TokenMemberVariableNotAllowed,
                message:
                $"Cannot store {GetTokenKindDescription(type: type)} in member variable '{memberVariableName}'. Tokens are inline-only and cannot be stored.",
                location: location);
        }
    }

    /// <summary>
    /// Validates that exclusive tokens (Claiming) are not passed multiple times in a single call.
    /// </summary>
    private void ValidateExclusiveTokenUniqueness(List<Expression> arguments,
        SourceLocation location)
    {
        if (_registry.Language != Language.RazorForge)
        {
            return; // Token validation only applies to RazorForge
        }

        // Track which exclusive token expressions we've seen
        var seenExclusiveTokens = new HashSet<string>();

        foreach (Expression arg in arguments)
        {
            // Get the expression's type
            if (arg.ResolvedType == null)
            {
                continue;
            }

            // Convert AST TypeInfo back to get the type name
            string baseName = arg.ResolvedType.BareName;

            if (!ExclusiveTokenTypes.Contains(value: baseName))
            {
                continue;
            }

            // Get a string representation of the expression for uniqueness checking
            string? exprKey = GetExpressionKey(expression: arg);
            if (string.IsNullOrEmpty(value: exprKey))
            {
                continue;
            }

            if (!seenExclusiveTokens.Add(item: exprKey))
            {
                ReportError(code: SemanticDiagnosticCode.ExclusiveTokenDuplicate,
                    message:
                    $"Cannot pass the same {baseName} token '{exprKey}' multiple times in a single call. Exclusive tokens require unique access.",
                    location: location);
            }
        }
    }

    /// <summary>
    /// Validates that every argument crossing an async spawn boundary can do so soundly. Under M:N
    /// both a <c>threaded routine</c> (OS thread) and a <c>suspended routine</c> (coroutine that may
    /// migrate to any worker) are potentially parallel, so the SAME crossing rule applies to both:
    /// an argument is safe when it is
    /// <list type="bullet">
    /// <item><c>steal</c>-moved — exclusive transfer, the caller loses access (safe regardless of
    /// type — a moved bare entity / <c>Retained</c> leaves exactly one live handle);</item>
    /// <item>trivially copyable — passed BY VALUE as an independent copy;</item>
    /// <item>a thread-shareable wrapper (<c>Atomic</c>/<c>Guarded</c>/<c>Witnessed</c>/<c>Consulting</c>/
    /// <c>Claiming</c>) — carries its own synchronization (atomic refcount or lock).</item>
    /// </list>
    /// A parameter that is none of these <em>and</em> is not steal-moved at the call site — e.g. a
    /// bare entity or a record transitively owning a single-threaded wrapper
    /// (<c>Retained</c>/<c>Tracked</c>/<c>Viewing</c>/<c>Modifying</c>) passed by copy — would
    /// silently alias unsynchronized state across parallel coroutines (non-atomic refcount / unsynced
    /// access = UAF), so it is rejected (RF-S632). Synchronization must be MARKED, never implied
    /// (same ethos as <c>steal</c>/<c>!</c>/overflow). Applying this at the <c>suspended</c> boundary
    /// (previously unchecked — harmless when single-threaded) is required for M:N soundness; crediting
    /// <c>steal</c> also removes the old over-strictness at the <c>threaded</c> boundary.
    /// </summary>
    private void ValidateAsyncRoutineArguments(RoutineInfo routine,
        IReadOnlyList<Expression> arguments, string boundaryKind, SourceLocation location)
    {
        if (_registry.Language != Language.RazorForge)
        {
            return;
        }

        HashSet<string> stolenParams = CollectStolenParameters(routine: routine,
            arguments: arguments);

        foreach (ParameterInfo param in routine.Parameters)
        {
            ValidateAsyncRoutineParameter(param: param,
                stolenParams: stolenParams,
                boundaryKind: boundaryKind,
                location: location);
        }
    }

    /// <summary>
    /// Validates a single parameter of an async-spawn routine against the M:N crossing rule, reporting
    /// RF-S632 when the parameter can neither be trivially copied, steal-moved, nor carries its own
    /// synchronization. Extracted from <see cref="ValidateAsyncRoutineArguments"/>.
    /// </summary>
    private void ValidateAsyncRoutineParameter(ParameterInfo param, HashSet<string> stolenParams,
        string boundaryKind, SourceLocation location)
    {
        TypeSymbol type = param.Type;
        if (type is ErrorTypeInfo || IsThreadShareable(type: type))
        {
            return;
        }

        // A `Roamed` handle crosses the boundary by being PROMOTED, not rejected: passing it across
        // a concurrency boundary IS the escape event, and codegen inserts `promote()` on the arg
        // before the spawn (LOCAL -> ESCAPED: atomic refcount + armed reentrant lock). So the same
        // object is thread-safe by the time the callee touches it — accepted here, no RF-S632.
        if (type.BareName == Declaration.RuntimeContract.Roamed)
        {
            return;
        }

        // A bare entity is a heap handle; passing it by copy copies the pointer, so the same
        // object would be aliased across parallel coroutines/threads. A record/tuple that
        // transitively owns a single-threaded RC wrapper (Retained/Tracked) or a scoped token
        // would alias its interior the same way. Pure value data has neither and is copied
        // safely. (Structural walk — does NOT depend on `Assignable` protocol population, which
        // is not attached to the resolved parameter-type instances reached here.)
        bool isEntity = type is EntityTypeInfo;

        // `steal` credits ONLY a bare entity: it is single-owner, so a move leaves exactly one
        // live handle (provably exclusive — the caller loses access). It does NOT credit a type
        // that (transitively) owns a single-threaded RC wrapper: moving one `Retained`/`Tracked`
        // handle does not prove no siblings exist, and a bare `Retained`/`Tracked` cannot be
        // `steal`-moved at all (RF-S617). Such a value must cross via `Guarded`/`Witnessed` (atomic).
        if (isEntity && stolenParams.Contains(item: param.Name))
        {
            return;
        }

        (string Wrapper, string Path)? offender = isEntity
            ? null
            : FindNonTriviallyAssignableWrapper(type: type);
        if (!isEntity && offender == null)
        {
            return;
        }

        string reason = isEntity
            ? "a bare entity aliases the same object across parallel coroutines"
            : $"it transitively owns `{offender!.Value.Wrapper}` at `{offender.Value.Path}`";
        string fix = isEntity
            ? "`steal`-move it, share it with `Guarded`/`Witnessed`/`Atomic`/`Consulting`/`Claiming`, " +
              "or pass a copyable value"
            : "share it with `Guarded`/`Witnessed`/`Atomic`/`Consulting`/`Claiming`, or pass a copyable value";
        ReportError(code: SemanticDiagnosticCode.ThreadArgNotShareable,
            message:
            $"Parameter `{param.Name}: {type.Name}` of a {boundaryKind} routine cannot cross the " +
            $"spawn boundary safely — {reason}. {fix}.",
            location: location);
    }

    /// <summary>
    /// Returns the set of parameter names whose call-site argument is a <c>steal</c> move (an
    /// exclusive ownership transfer). Such arguments cross an async spawn boundary safely regardless
    /// of type, because the caller loses access — exactly one live handle survives. Handles both
    /// positional arguments (matched by position) and <c>NamedArgumentExpression</c> (matched by
    /// name); named and positional are never mixed in a single call (RF-S512).
    /// </summary>
    private static HashSet<string> CollectStolenParameters(RoutineInfo routine,
        IReadOnlyList<Expression> arguments)
    {
        var stolen = new HashSet<string>(comparer: StringComparer.Ordinal);
        int positional = 0;
        foreach (Expression arg in arguments)
        {
            if (arg is NamedArgumentExpression named)
            {
                if (named.Value is StealExpression)
                {
                    stolen.Add(item: named.Name);
                }

                continue;
            }

            if (positional < routine.Parameters.Count && arg is StealExpression)
            {
                stolen.Add(item: routine.Parameters[index: positional].Name);
            }

            positional++;
        }

        return stolen;
    }

    /// <summary>
    /// Gets a string key representing an expression for uniqueness checking.
    /// Returns null for complex expressions that can't be easily tracked.
    /// </summary>
    private static string? GetExpressionKey(Expression expression)
    {
        return expression switch
        {
            IdentifierExpression id => id.Name,
            MemberExpression member =>
                $"{GetExpressionKey(expression: member.Object)}.{member.MemberName}",
            _ => null
        };
    }

    /// <summary>
    /// Validates that a type is not an inline-only token when used as a variant case payload.
    /// </summary>
    internal void ValidateNotTokenVariantPayload(TypeSymbol type, string caseName,
        SourceLocation location)
    {
        if (_registry.Language != Language.RazorForge)
        {
            return; // Token validation only applies to RazorForge
        }

        if (IsInlineOnlyTokenType(type: type))
        {
            ReportError(code: SemanticDiagnosticCode.TokenVariantPayloadNotAllowed,
                message:
                $"Cannot use {GetTokenKindDescription(type: type)} as payload for variant case '{caseName}'. Tokens are inline-only and cannot be stored in variants.",
                location: location);
        }
    }

    /// <summary>
    /// Gets the effective visibility for member variable write access.
    /// For posted member variables, write access is secret (only owner can write).
    /// </summary>
    /// <param name="memberVariable">The member variable to check.</param>
    /// <returns>The effective visibility for write access.</returns>
    private static VisibilityModifier GetEffectiveWriteVisibility(
        MemberVariableInfo memberVariable)
    {
        // Posted member variables have open read but secret write
        return memberVariable.Visibility == VisibilityModifier.Posted
            ? VisibilityModifier.Secret
            : memberVariable.Visibility;
    }

    /// <summary>
    /// Checks if access to a member variable is allowed from the current context.
    /// </summary>
    /// <param name="memberVariable">The member variable being accessed.</param>
    /// <param name="isWrite">Whether this is a write access (assignment).</param>
    /// <param name="accessLocation">Source location of the access site.</param>
    private void ValidateMemberVariableAccess(MemberVariableInfo memberVariable, bool isWrite,
        SourceLocation accessLocation)
    {
        // Posted member variables: open read, module-only write
        if (isWrite && memberVariable.Visibility == VisibilityModifier.Posted &&
            !IsAccessingFromSameModule(memberModule: memberVariable.Owner?.Module))
        {
            string typeName = memberVariable.Owner?.Name ?? "type";
            ReportError(code: SemanticDiagnosticCode.PostedMemberAccess,
                message:
                $"Cannot write to posted member variable '{memberVariable.Name}' of '{typeName}' from outside its module.",
                location: accessLocation);
            return;
        }

        // For posted member variables, write access is restricted to secret (module only)
        VisibilityModifier visibility = isWrite
            ? GetEffectiveWriteVisibility(memberVariable: memberVariable)
            : memberVariable.Visibility;

        ValidateMemberAccess(visibility: visibility,
            memberKind: "member variable",
            memberName: memberVariable.Name,
            ownerType: memberVariable.Owner,
            accessLocation: accessLocation);
    }

    /// <summary>
    /// Checks if access to a routine is allowed from the current context.
    /// </summary>
    /// <param name="routine">The routine being accessed.</param>
    /// <param name="accessLocation">Source location of the access site.</param>
    /// <param name="isCompilerSynthesized">When true, suppresses the dangerous-outside-danger-block
    /// check for compiler-injected calls (e.g. <c>local.destroy()</c> from teardown lowering)
    /// that are not user-written and must not be gated by a danger block.</param>
    private void ValidateRoutineAccess(RoutineInfo routine, SourceLocation accessLocation,
        bool isCompilerSynthesized = false)
    {
        ValidateMemberAccess(visibility: routine.Visibility,
            memberKind: routine.Kind switch
            {
                RoutineKind.Creator => "creator",
                RoutineKind.MemberRoutine => "member routine",
                _ => "routine"
            },
            memberName: routine.Name,
            ownerType: routine.OwnerType,
            accessLocation: accessLocation);

        // Dangerous routines can only be called inside danger blocks — EXCEPT a compiler-synthesized
        // call (e.g. ScopeTeardownLoweringPass's injected `local.destroy()`), which is the compiler's
        // own teardown, not user code invoking a dangerous routine. Under the demand pipeline teardown is
        // injected before the stdlib body is SA'd on reach, so without this exemption the re-analysis
        // would false-fire RF-S609 on every scope-exit destroy (the eager pipeline SA'd stdlib first).
        if (routine.IsDangerous && !InDangerBlock && !isCompilerSynthesized)
        {
            ReportError(code: SemanticDiagnosticCode.DangerousCallOutsideDangerBlock,
                message:
                $"Dangerous routine '{routine.Name}' can only be called inside a 'danger' block.",
                location: accessLocation);
        }
    }

    /// <summary>
    /// Validates access to a member based on visibility rules.
    /// </summary>
    /// <param name="visibility">The visibility modifier of the member.</param>
    /// <param name="memberKind">The kind of member (member variable, memberRoutine, etc.) for error messages.</param>
    /// <param name="memberName">The name of the member.</param>
    /// <param name="ownerType">The type that owns this member, if any.</param>
    /// <param name="accessLocation">Source location of the access site.</param>
    private void ValidateMemberAccess(VisibilityModifier visibility, string memberKind,
        string memberName, TypeSymbol? ownerType, SourceLocation accessLocation)
    {
        // Owner secrecy CAPS member visibility: a `secret` (module-private) type's members are
        // module-private too, no matter their own modifier. The type name is already hidden cross-module,
        // but an importer can still obtain an instance by inference through a non-secret factory that
        // returns it — this closes that hole. No per-member `secret` annotation is required.
        if (ownerType is { Visibility: VisibilityModifier.Secret } &&
            !IsAccessingFromSameModule(memberModule: ownerType.Module))
        {
            ReportError(code: SemanticDiagnosticCode.SecretMemberAccess,
                message:
                $"Cannot access {memberKind} '{memberName}' of secret (module-private) type '{ownerType.Name}' " +
                $"from outside its module.",
                location: accessLocation);
            return;
        }

        switch (visibility)
        {
            case VisibilityModifier.Secret:
                // Secret members are accessible within the same module
                if (!IsAccessingFromSameModule(memberModule: ownerType?.Module))
                {
                    string typeName = ownerType?.Name ?? "type";
                    ReportError(code: SemanticDiagnosticCode.SecretMemberAccess,
                        message:
                        $"Cannot access secret {memberKind} '{memberName}' of '{typeName}' from outside its module.",
                        location: accessLocation);
                }

                break;

            case VisibilityModifier.Posted:
            case VisibilityModifier.Open:
            case VisibilityModifier.External:
                // Open/Posted/External members are accessible from anywhere for reading
                break;
        }
    }

    /// <summary>
    /// Checks if the current access context is within the same module as the member.
    /// Module comparison is exact (sub-modules are different modules).
    /// </summary>
    private bool IsAccessingFromSameModule(string? memberModule)
    {
        string? currentModuleName = GetCurrentModuleName();

        // If both are in no module, they're in the same module
        if (string.IsNullOrEmpty(value: memberModule) &&
            string.IsNullOrEmpty(value: currentModuleName))
        {
            return true;
        }

        // If either is null/empty but not both, they're not in the same module
        if (string.IsNullOrEmpty(value: memberModule) ||
            string.IsNullOrEmpty(value: currentModuleName))
        {
            return false;
        }

        // Module comparison is exact - sub-modules are different modules
        return currentModuleName == memberModule;
    }

    /// <summary>
    /// Validates write access to a member variable, checking setter visibility.
    /// </summary>
    /// <param name="objectType">The type of the object being accessed.</param>
    /// <param name="memberVariableName">The name of the member variable being written.</param>
    /// <param name="location">The source location of the write.</param>
    private void ValidateMemberVariableWriteAccess(TypeSymbol objectType,
        string memberVariableName, SourceLocation location)
    {
        if (TryUnwrapMarkerReceiver(type: objectType, innerType: out TypeSymbol targetType))
        {
            if (IsReadOnlyTransparentProtocol(type: objectType) ||
                IsReadOnlyMarkerBoundParam(type: objectType))
            {
                ReportError(code: SemanticDiagnosticCode.WriteThroughReadOnlyWrapper,
                    message:
                    $"Cannot write to member '{memberVariableName}' through read-only protocol '{objectType.Name}'. " +
                    "Use Controlling[T] or a writable token instead.",
                    location: location);
                return;
            }

            objectType = targetType;
        }

        MemberVariableInfo? memberVariable = objectType switch
        {
            RecordTypeInfo record => record.LookupMemberVariable(
                memberVariableName: memberVariableName),
            EntityTypeInfo entity => entity.LookupMemberVariable(
                memberVariableName: memberVariableName),
            _ => null
        };

        if (memberVariable != null)
        {
            ValidateMemberVariableAccess(memberVariable: memberVariable,
                isWrite: true,
                accessLocation: location);
        }
    }

    /// <summary>
    /// Checks whether a file path is inside the stdlib directory.
    /// Used to allow stdlib files to use reserved features (e.g., module Core).
    /// </summary>
    internal bool IsStdlibFile(string filePath)
    {
        string? stdlibPath = _registry.StdlibPath;
        if (string.IsNullOrEmpty(value: stdlibPath) || string.IsNullOrEmpty(value: filePath))
        {
            return false;
        }

        string normalizedFile = Path.GetFullPath(path: filePath);
        string normalizedStdlib = Path.GetFullPath(path: stdlibPath);
        return normalizedFile.StartsWith(value: normalizedStdlib,
            comparisonType: StringComparison.OrdinalIgnoreCase);
    }
}
