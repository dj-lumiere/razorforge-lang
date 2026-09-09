using Compiler.Diagnostics;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Compiler.Verification;

using TypeSymbol = TypeInfo;

public sealed partial class SemanticVerifier
{
    private const string ModifyingWrapperName = Declaration.RuntimeContract.Modifying;
    private const string AmendingWrapperName = Declaration.RuntimeContract.Amending;
    private const string ViewingWrapperName = Declaration.RuntimeContract.Viewing;
    private const string ConsultingWrapperName = Declaration.RuntimeContract.Consulting;
    private const string ScopedNoEscapeHint = "(none — scoped, can't escape)";
    private const string ShareVerb = "a.share()";

    private bool IsNestedModifying(Expression source)
    {
        while (true)
        {
            // Check if source is a member access expression (e.g., p.child)
            if (source is not MemberExpression member)
            {
                return false;
            }

            // Check if the object being accessed is an identifier
            if (member.Object is not IdentifierExpression id)
            {
                // Could be a chained member access, check recursively
                source = member.Object;
                continue;
            }

            // Look up the variable and check if its type is Modifying<T>
            VariableInfo? varInfo = _registry.LookupVariable(name: id.Name);
            return varInfo != null &&
                   // Check if the variable's type is Modifying<T>
                   IsModifyingType(type: varInfo.Type);
        }
    }

    /// <summary>
    /// Checks if a type is a Modifying&lt;T&gt; token type.
    /// </summary>
    private static bool IsModifyingType(TypeSymbol type)
    {
        return type.Name == ModifyingWrapperName || type.Name.StartsWith(value: ModifyingWrapperName + "[");
    }

    /// <summary>
    /// Checks if a type is a Claiming&lt;T&gt; token type.
    /// </summary>
    private static bool IsAmendingType(TypeSymbol type)
    {
        return type.Name == AmendingWrapperName || type.Name.StartsWith(value: AmendingWrapperName + "[");
    }

    /// <summary>
    /// Checks if a type is a Guarded&lt;T&gt; handle type.
    /// </summary>
    private static bool IsSharedType(TypeSymbol type)
    {
        return type.Name == Declaration.RuntimeContract.Guarded;
    }

    /// <summary>
    /// Checks if a type is a Witnessed&lt;T&gt; handle type.
    /// </summary>
    private static bool IsWatchedType(TypeSymbol type)
    {
        return type.Name == Declaration.RuntimeContract.Witnessed;
    }

    /// <summary>
    /// All wrapper types that transparently forward to their inner type. Single source of truth is
    /// <see cref="Compiler.Declaration.RuntimeContract.WrapperTypes"/> — do NOT re-list the members here
    /// (a local copy silently drifts when a wrapper is added/renamed).
    /// </summary>
    private static readonly IReadOnlySet<string> WrapperTypes =
        Declaration.RuntimeContract.WrapperTypes;

    /// <summary>
    /// Read-only wrapper types that can only access @readonly memberRoutines. Single source of truth is
    /// <see cref="Compiler.Declaration.RuntimeContract.ReadOnlyWrapperTypes"/>.
    /// </summary>
    private static readonly IReadOnlySet<string> ReadOnlyWrapperTypes =
        Declaration.RuntimeContract.ReadOnlyWrapperTypes;

    /// <summary>
    /// Checks if a type is a wrapper type (Viewing, Modifying, Guarded, etc.).
    /// </summary>
    /// <param name="type">The type to check.</param>
    /// <returns>True if the type is a wrapper type.</returns>
    private static bool IsWrapperType(TypeSymbol type)
    {
        string baseName = type.BareName;
        return WrapperTypes.Contains(value: baseName);
    }

    /// <summary>
    /// Checks if a wrapper type is read-only (Viewing, Consulting).
    /// </summary>
    /// <param name="type">The wrapper type to check.</param>
    /// <returns>True if the wrapper is read-only.</returns>
    private static bool IsReadOnlyWrapper(TypeSymbol type)
    {
        string baseName = type.BareName;
        return ReadOnlyWrapperTypes.Contains(value: baseName);
    }

    /// <summary>
    /// Gets the inner type from a wrapper type (e.g., T from Viewing&lt;T&gt;).
    /// </summary>
    /// <param name="wrapperType">The wrapper type.</param>
    /// <returns>The inner type, or null if not a wrapper or no type arguments.</returns>
    private static TypeSymbol? GetWrapperInnerType(TypeSymbol wrapperType)
    {
        if (!IsWrapperType(type: wrapperType))
        {
            return null;
        }

        // Wrapper types have their inner type as the first type argument
        if (wrapperType.TypeArguments is { Count: > 0 })
        {
            return wrapperType.TypeArguments[index: 0];
        }

        return null;
    }

    /// <summary>
    /// Tries to look up a member variable on the inner type of a wrapper type.
    /// </summary>
    /// <param name="wrapperType">The wrapper type.</param>
    /// <param name="memberVariableName">The name of the member variable to look up.</param>
    /// <returns>The member variable info if found, null otherwise.</returns>
    private static MemberVariableInfo? LookupMemberVariableOnWrapperInnerType(TypeSymbol wrapperType,
        string memberVariableName)
    {
        TypeSymbol? innerType = GetWrapperInnerType(wrapperType: wrapperType);
        if (innerType == null)
        {
            return null;
        }

        return innerType switch
        {
            RecordTypeInfo record => record.LookupMemberVariable(
                memberVariableName: memberVariableName),
            EntityTypeInfo entity => entity.LookupMemberVariable(
                memberVariableName: memberVariableName),
            _ => null
        };
    }

    /// <summary>
    /// Validates that a memberRoutine can be called through a read-only wrapper.
    /// Read-only wrappers (Viewing, Consulting) can only call @readonly memberRoutines.
    /// </summary>
    /// <param name="wrapperType">The wrapper type being used.</param>
    /// <param name="memberRoutine">The memberRoutine being called.</param>
    /// <param name="location">Source location for error reporting.</param>
    private void ValidateReadOnlyWrapperMemberRoutineAccess(TypeSymbol wrapperType, RoutineInfo memberRoutine,
        SourceLocation location)
    {
        if (!IsReadOnlyWrapper(type: wrapperType))
        {
            return; // Modifiable wrappers can access all memberRoutines
        }

        // Read-only wrappers can only access @readonly memberRoutines
        if (!memberRoutine.IsReadOnly)
        {
            string wrapperName = wrapperType.BareName;
            ReportError(code: SemanticDiagnosticCode.WritableMemberRoutineThroughReadOnlyWrapper,
                message:
                $"Cannot call writable member routine '{memberRoutine.Name}' through read-only wrapper '{wrapperName}[T]'. " +
                $"Only @readonly member routines are accessible.",
                location: location);
        }
    }

    /// <summary>
    /// Token types that cannot be returned from routines or stored in member variables.
    /// These are inline-only access tokens that must stay within their scope.
    /// </summary>
    private static readonly HashSet<string> InlineOnlyTokenTypes =
    [
        ViewingWrapperName, // Read-only single-threaded token
        ModifyingWrapperName, // Mutable (non-exclusive) single-threaded token
        ConsultingWrapperName, // Read-only multi-threaded token
        AmendingWrapperName // Exclusive write multi-threaded token
    ];

    /// <summary>
    /// Token types that require uniqueness validation (cannot be passed twice in same call).
    /// Only the multi-threaded exclusive write token qualifies: single-threaded Modifying is
    /// non-exclusive (shared-mutable aliasing is harmless without a second thread).
    /// </summary>
    private static readonly HashSet<string> ExclusiveTokenTypes =
    [
        AmendingWrapperName // Cannot pass same Claiming token twice
    ];

    /// <summary>
    /// Wrapper types that require an explicit verb at every copy site instead of being
    /// implicitly assignable. The dictionary value is the verb the user must write.
    /// `Hijacked[T]` is excluded because it is a raw pointer and copies bitwise.
    /// See <c>RazorForge-Wiki/docs/Records.md#copy-semantics</c>.
    /// </summary>
    private static readonly Dictionary<string, string> NonTriviallyAssignableWrappers =
        new(StringComparer.Ordinal)
        {
            [Declaration.RuntimeContract.Retained] = ShareVerb,
            [Declaration.RuntimeContract.Tracked] = ShareVerb,
            [Declaration.RuntimeContract.Guarded] = ShareVerb,
            [Declaration.RuntimeContract.Witnessed] = ShareVerb,
            [ViewingWrapperName] = ScopedNoEscapeHint,
            [ModifyingWrapperName] = ScopedNoEscapeHint,
            [ConsultingWrapperName] = ScopedNoEscapeHint,
            [AmendingWrapperName] = ScopedNoEscapeHint,
        };

    /// <summary>
    /// Returns true when the type can appear in an implicit-copy position (var binding,
    /// non-<c>steal</c> argument pass, non-<c>T</c> return, <c>with</c> base). The check
    /// is "obeys <c>Assignable</c>" — auto-derived for records whose @llvm layout has no
    /// <c>ptr</c>, explicitly opt-in for raw-pointer wrappers (<c>Hijacked</c>, <c>CPtr</c>),
    /// never auto-derived for ownership-bearing wrappers (<c>Retained</c>,
    /// <c>Tracked</c>, scoped tokens). The recursive structural walk that this used to do
    /// is now subsumed by the protocol's auto-derivation rule.
    /// </summary>
    /// <summary>
    /// True when a type carries its own cross-thread synchronization and may therefore cross an
    /// async spawn boundary (<c>threaded</c> OR <c>suspended</c> under M:N — both are potentially
    /// parallel) by reference, aliasing the spawner's cell safely. These are the atomic /
    /// shared-ownership wrappers — <c>Atomic[T]</c>, <c>Guarded[T,P]</c>, <c>Witnessed[T,P]</c> (atomic
    /// refcount) — plus the <em>multi-threaded</em> lock-backed tokens <c>Consulting[T,P]</c>
    /// (read-only) and <c>Claiming[T,P]</c> (exclusive), whose mutex/rwlock makes concurrent access
    /// sound. The single-threaded tokens <c>Viewing</c>/<c>Modifying</c> are deliberately NOT here —
    /// they are unsynchronized (see the 2×2 in <c>internal-wiki/v0.3.x-mn-scheduler.md</c> §4). Every
    /// other type must be trivially copyable (passed by value as an independent copy) or
    /// <c>steal</c>-moved so unsynchronized state can never alias across the boundary.
    /// </summary>
    private static bool IsThreadShareable(TypeSymbol type) =>
        type.BareName is Declaration.RuntimeContract.Atomic
            or Declaration.RuntimeContract.Guarded or Declaration.RuntimeContract.Witnessed
            or Declaration.RuntimeContract.Consulting or Declaration.RuntimeContract.Amending;

    private static bool IsTriviallyAssignable(TypeSymbol type)
    {
        if (type is ErrorTypeInfo or GenericParameterTypeInfo || type.IsNone)
        {
            // Unknown / void types — be permissive so we do not double-report.
            return true;
        }

        // Generic-definition wrappers / records (no concrete type args) appear when SA walks
        // generic-def bodies. The concrete instantiations are re-analysed via monomorphisation,
        // so suppress here to avoid duplicate / placeholder-shaped diagnostics on stdlib.
        if (type is RecordTypeInfo { IsGenericDefinition: true, TypeArguments: null or { Count: 0 } })
        {
            return true;
        }

        // Tuples — anonymous; auto-derive cascades only when every element does.
        if (type is TupleTypeInfo tuple)
        {
            return tuple.ElementTypes.All(predicate: IsTriviallyAssignable);
        }

        // Records / choices / flags / entities carry ImplementedProtocols populated by
        // ProtocolConformanceAnalyzer (explicit + auto-derived Assignable).
        List<TypeSymbol>? implemented = type switch
        {
            ChoiceTypeInfo c => c.ImplementedProtocols,
            FlagsTypeInfo f => f.ImplementedProtocols,
            RecordTypeInfo r => r.ImplementedProtocols,
            EntityTypeInfo e => e.ImplementedProtocols,
            _ => null
        };

        if (implemented != null)
        {
            // Either capability qualifies: `Assignable` (can `store`) or `Copyable` (deep copy, which
            // obeys Assignable). The auto-derive adds `Copyable` directly (not `Assignable`), so this direct
            // name check must accept both.
            return implemented.Any(predicate: p => p.Name is "Assignable" or "Copyable");
        }

        // Anything we did not recognise falls back to trivially copyable so this pass does
        // not become noisy on unexpected shapes.
        return true;
    }

    /// <summary>
    /// Locates the first non-trivially-copyable wrapper inside a type's field tree.
    /// Used to format the hint message for the implicit-copy diagnostic.
    /// Returns null when the type itself is trivially copyable.
    /// </summary>
    /// <param name="type">Type to classify.</param>
    /// <returns>The offending wrapper's base name (e.g. <c>"Retained"</c>) and the path
    /// of field names leading to it, or null when no offender exists.</returns>
    private static (string Wrapper, string Path)? FindNonTriviallyAssignableWrapper(TypeSymbol type)
    {
        return FindNonTriviallyAssignableWrapperCore(
            type: type, prefix: "", visited: new HashSet<string>(StringComparer.Ordinal));
    }

    private static (string, string)? FindNonTriviallyAssignableWrapperCore(
        TypeSymbol type, string prefix, HashSet<string> visited)
    {
        string baseName = type.BareName;
        if (NonTriviallyAssignableWrappers.ContainsKey(key: baseName))
            return (baseName, prefix.Length == 0 ? "<value>" : prefix);

        if (type is RecordTypeInfo record &&
            !(record is { IsGenericDefinition: true, TypeArguments: not { Count: > 0 } }))
        {
            return FindInRecord(record: record, prefix: prefix, visited: visited);
        }

        if (type is TupleTypeInfo tuple)
        {
            return FindInTuple(tuple: tuple, prefix: prefix, visited: visited);
        }

        return null;
    }

    private static (string, string)? FindInRecord(
        RecordTypeInfo record, string prefix, HashSet<string> visited)
    {
        if (!visited.Add(item: record.FullName))
            return null;
        foreach (MemberVariableInfo member in record.MemberVariables)
        {
            string childPath = prefix.Length == 0 ? member.Name : $"{prefix}.{member.Name}";
            var found = FindNonTriviallyAssignableWrapperCore(
                type: member.Type, prefix: childPath, visited: visited);
            if (found != null) return found;
        }
        return null;
    }

    private static (string, string)? FindInTuple(
        TupleTypeInfo tuple, string prefix, HashSet<string> visited)
    {
        if (!visited.Add(item: tuple.FullName))
            return null;
        for (int i = 0; i < tuple.ElementTypes.Count; i++)
        {
            string childPath = prefix.Length == 0 ? $".{i}" : $"{prefix}.{i}";
            var found = FindNonTriviallyAssignableWrapperCore(
                type: tuple.ElementTypes[index: i], prefix: childPath, visited: visited);
            if (found != null) return found;
        }
        return null;
    }
}
