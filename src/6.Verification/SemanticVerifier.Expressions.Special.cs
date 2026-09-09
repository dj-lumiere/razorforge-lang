using Compiler.Diagnostics;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Compiler.Verification;

using TypeSymbol = TypeInfo;

public sealed partial class SemanticVerifier
{
    private TypeSymbol AnalyzeInsertedTextExpression(InsertedTextExpression insertedText)
    {
        foreach (InsertedTextPart part in insertedText.Parts)
        {
            if (part is ExpressionPart exprPart)
            {
                AnalyzeExpression(expression: exprPart.Expression);
                ValidateFTextFormatSpec(formatSpec: exprPart.FormatSpec,
                    location: exprPart.Location);
            }
        }

        return _registry.LookupType(name: "Text") ?? ErrorTypeInfo.Instance;
    }

    /// <summary>
    /// Validates that an f-text format specifier is one of the allowed values.
    /// Valid: null (none), "=", "?", "=?". Invalid: "?=" (wrong order), anything else.
    /// </summary>
    private void ValidateFTextFormatSpec(string? formatSpec, SourceLocation location)
    {
        if (formatSpec is null or "=" or "?" or "=?")
        {
            return;
        }

        if (formatSpec == "?=")
        {
            ReportError(code: SemanticDiagnosticCode.InvalidFTextFormatSpec,
                message:
                "Invalid f-text format specifier '?='. The correct order is '=?' (name display first, then diagnose).",
                location: location);
            return;
        }

        ReportError(code: SemanticDiagnosticCode.InvalidFTextFormatSpec,
            message:
            $"Invalid f-text format specifier '{formatSpec}'. F-text only supports '=' (name display), '?' (diagnose), and '=?' (combined).",
            location: location);
    }

/// <summary>
    /// Substitutes type parameters in a type based on a generic resolution.
    /// For example, if genericType is List&lt;S32&gt; and type is T, returns S32.
    /// </summary>
    /// <param name="type">The type that may contain type parameters.</param>
    /// <param name="genericType">The resolved generic type providing type argument bindings.</param>
    /// <returns>The substituted type.</returns>
    private TypeSymbol SubstituteTypeParameters(TypeSymbol type, TypeSymbol genericType)
    {
        if (genericType.TypeArguments == null || genericType.TypeArguments.Count == 0)
        {
            return type;
        }

        // Get the generic definition to find type parameter names
        TypeSymbol? genericDef = GetGenericDefinition(resolution: genericType);
        if (genericDef == null)
        {
            return type;
        }

        // Build a mapping from type parameter names to actual types
        List<string>? typeParamNames = genericDef.GenericParameters;
        if (typeParamNames == null || typeParamNames.Count != genericType.TypeArguments.Count)
        {
            return type;
        }

        var substitutions = new Dictionary<string, TypeSymbol>();
        for (int i = 0; i < typeParamNames.Count; i++)
        {
            substitutions[key: typeParamNames[index: i]] = genericType.TypeArguments[index: i];
        }

        return SubstituteWithMapping(type: type, substitutions: substitutions);
    }

    /// <summary>
    /// Gets the generic definition from a resolution.
    /// </summary>
    private TypeSymbol? GetGenericDefinition(TypeSymbol resolution)
    {
        if (!resolution.IsGenericResolution)
        {
            return null;
        }

        // Extract base name (e.g., "List" from "List[S32]")
        string baseName = resolution.BareName;
        TypeSymbol? def = _registry.LookupType(name: baseName);
        // Try slash-qualified module path lookup for non-Core types (e.g., "Collections/CircularList")
        if (def == null && !string.IsNullOrEmpty(value: resolution.Module))
        {
            def = _registry.LookupType(name: $"{resolution.Module}.{baseName}");
        }

        return def;
    }

    /// <summary>
    /// Substitutes type parameters using a mapping. Dispatches to type-specific helpers so each
    /// structural case (projection, generic resolution, routine type, tuple) remains focused.
    /// </summary>
    private TypeSymbol SubstituteWithMapping(TypeSymbol type,
        Dictionary<string, TypeSymbol> substitutions)
    {
        // Associated-type projection (`S/Iter`): substitute the base, then resolve via its binding.
        if (type is AssociatedProjectionTypeInfo proj)
            return SubstituteProjection(proj: proj, substitutions: substitutions);

        // Direct type parameter replacement (covers ProtocolSelf via its Name "Me").
        if (substitutions.TryGetValue(key: type.Name, value: out TypeSymbol? replacement))
            return replacement;

        // For generic resolutions, recursively substitute in type arguments.
        if (type is { IsGenericResolution: true, TypeArguments: not null })
        {
            TypeSymbol? resolved = SubstituteGenericResolution(type: type, substitutions: substitutions);
            if (resolved != null) return resolved;
        }

        // Routine types: substitute inside parameter and return types.
        if (type is RoutineTypeInfo routineType)
        {
            TypeSymbol? resolved = SubstituteRoutineType(routineType: routineType, substitutions: substitutions);
            if (resolved != null) return resolved;
        }

        // Tuple types: substitute inside element types.
        if (type is TupleTypeInfo tupleType)
        {
            TypeSymbol? resolved = SubstituteTupleType(tupleType: tupleType, substitutions: substitutions);
            if (resolved != null) return resolved;
        }

        return type;
    }

    /// <summary>
    /// Substitutes through an <see cref="AssociatedProjectionTypeInfo"/>: substitutes the base,
    /// then resolves the associated binding if one is now available, or rebuilds the projection.
    /// Done at the call site so a member-routine return like <c>?EnumerateEmitter[T, S/Iter]</c>
    /// resolves to the CONCRETE emitter — otherwise reachability marks the unresolved-projection
    /// emitter's member routines and the concrete ones never generate.
    /// </summary>
    private TypeSymbol SubstituteProjection(AssociatedProjectionTypeInfo proj,
        Dictionary<string, TypeSymbol> substitutions)
    {
        TypeSymbol newBase = SubstituteWithMapping(type: proj.Base, substitutions: substitutions);
        TypeInfo? bound = RecordTypeInfo.ProjectAssociatedBinding(baseType: newBase,
            slot: proj.SlotName);
        if (bound != null)
            return SubstituteWithMapping(type: bound, substitutions: substitutions);
        return ReferenceEquals(objA: newBase, objB: proj.Base)
            ? proj
            : new AssociatedProjectionTypeInfo(baseType: newBase, slotName: proj.SlotName);
    }

    /// <summary>
    /// Substitutes into the type arguments of a generic resolution. Returns the new resolution
    /// when any argument changed, or null when no substitution was needed.
    /// </summary>
    private TypeSymbol? SubstituteGenericResolution(TypeSymbol type,
        Dictionary<string, TypeSymbol> substitutions)
    {
        var substitutedArgs = new List<TypeSymbol>();
        bool anyChanged = false;
        foreach (TypeSymbol arg in type.TypeArguments!)
        {
            TypeSymbol substitutedArg = SubstituteWithMapping(type: arg, substitutions: substitutions);
            substitutedArgs.Add(item: substitutedArg);
            if (!ReferenceEquals(objA: substitutedArg, objB: arg))
                anyChanged = true;
        }
        if (!anyChanged) return null;
        TypeSymbol? baseDef = GetGenericDefinition(resolution: type);
        return baseDef != null
            ? _registry.GetOrCreateResolution(genericDef: baseDef, typeArguments: substitutedArgs)
            : null;
    }

    /// <summary>
    /// Substitutes inside a routine type's parameter and return types so
    /// <c>Routine[(T, T), Bool]</c> becomes <c>Routine[(S64, S64), Bool]</c> for
    /// target-typing lambda arguments. Returns the new routine type when any slot changed,
    /// or null when no substitution was needed.
    /// </summary>
    private RoutineTypeInfo? SubstituteRoutineType(RoutineTypeInfo routineType,
        Dictionary<string, TypeSymbol> substitutions)
    {
        var newParams = new List<TypeInfo>(capacity: routineType.ParameterTypes.Count);
        bool anyChanged = false;
        foreach (TypeInfo p in routineType.ParameterTypes)
        {
            var substituted = SubstituteWithMapping(type: p, substitutions: substitutions);
            newParams.Add(item: substituted);
            if (!ReferenceEquals(objA: substituted, objB: p)) anyChanged = true;
        }
        TypeInfo? newReturn = routineType.ReturnType;
        if (newReturn != null)
        {
            var substitutedRet = SubstituteWithMapping(type: newReturn, substitutions: substitutions);
            if (!ReferenceEquals(objA: substitutedRet, objB: newReturn))
            {
                newReturn = substitutedRet;
                anyChanged = true;
            }
        }
        return anyChanged
            ? _registry.GetOrCreateRoutineType(parameterTypes: newParams,
                returnType: newReturn,
                isFailable: routineType.IsFailable)
            : null;
    }

    /// <summary>
    /// Substitutes inside a tuple type's element types. Returns the new tuple type when any
    /// element changed, or null when no substitution was needed.
    /// </summary>
    private TupleTypeInfo? SubstituteTupleType(TupleTypeInfo tupleType,
        Dictionary<string, TypeSymbol> substitutions)
    {
        var newElems = new List<TypeInfo>(capacity: tupleType.ElementTypes.Count);
        bool anyChanged = false;
        foreach (TypeInfo el in tupleType.ElementTypes)
        {
            var substituted = SubstituteWithMapping(type: el, substitutions: substitutions);
            newElems.Add(item: substituted);
            if (!ReferenceEquals(objA: substituted, objB: el)) anyChanged = true;
        }
        return anyChanged ? _registry.GetOrCreateTupleType(elementTypes: newElems) : null;
    }

    /// <summary>
    /// Analyzes a steal expression (RazorForge only).
    /// Validates that the operand can be stolen and returns the stolen type.
    /// </summary>
    /// <param name="steal">The steal expression to analyze.</param>
    /// <returns>The type of the stolen value.</returns>
    /// <remarks>
    /// Stealable types:
    /// - Raw entities (direct entity references)
    ///
    /// Non-stealable types (build error):
    /// - Viewing[T]    (read-only wrapper, scope-bound)
    /// - Modifying[T]  (exclusive wrapper, scope-bound)
    /// - Consulting[T] (thread-safe read wrapper, scope-bound)
    /// - Amending[T]   (thread-safe exclusive wrapper, scope-bound)
    /// - Retained[T]  (shared-ownership wrapper)
    /// - Tracked[T]   (reference-counted wrapper)
    /// - Guarded[T, P] (shared-ownership wrapper)
    /// - Witnessed[T, P] (reference-counted wrapper)
    /// - Hijacked[T]  (internal ownership wrapper)
    /// </remarks>
    private TypeSymbol AnalyzeStealExpression(StealExpression steal)
    {
        // Steal has side effects (deadref marking) and emits diagnostics. Overload-resolution
        // pre-passes can re-analyze argument expressions, which would re-emit errors and
        // double-process the steal. Cache the resolved type on the node so repeated analysis
        // of the same StealExpression is a no-op.
        if (steal.ResolvedType != null)
        {
            return steal.ResolvedType;
        }

        // Analyze the operand
        TypeSymbol operandType = AnalyzeExpression(expression: steal.Operand);

        // Aggregate-steal = hole: you cannot move a value OUT of an aggregate's MIDDLE. `steal l[i]`
        // leaves a dense List with no empty-slot state; `steal o.field` leaves a dangling struct hole.
        // Neither copy (two owners) nor steal is sound there — only a REPAIRING removal (`remove_at`,
        // which shifts and adjusts the count) or a shared handle extracts safely. A standalone
        // `steal <identifier>` (deadref'd, no hole) and `steal x.duplicate()` (a fresh owned rvalue
        // from a call — the operand is a CallExpression, not an aggregate-part read) stay legal. Tuple
        // element access (`_t.item0` from a `var (a, b) = …` destructure) is a consumed-temporary MOVE,
        // not an aggregate-middle steal, so it is excluded (Object is a TupleTypeInfo).
        bool isAggregatePart = steal.Operand switch
        {
            IndexExpression => true,
            MemberExpression m => m.Object.ResolvedType is not TupleTypeInfo,
            _ => false
        };
        if (isAggregatePart)
        {
            ReportError(code: SemanticDiagnosticCode.StealAggregatePart,
                message: "You are trying to steal a value out of the middle of an aggregate, which " +
                         "would leave a hole where it used to sit. Move it out with a repairing " +
                         "removal (e.g. 'remove_at', which closes the gap), or keep a shareable " +
                         "handle instead.",
                location: steal.Location);
            steal.ResolvedType = operandType;
            return operandType;
        }

        // Check the scope-bound / internal / shared-ownership wrapper rejections. Each reports its own
        // diagnostic; on any hit, analysis stops with the operand type.
        if (TryRejectUnstealableWrapper(steal: steal, operandType: operandType))
        {
            steal.ResolvedType = operandType;
            return operandType;
        }

        // T is explicitly stealable — ownership transfer is its design purpose
        bool isOwned = operandType is WrapperTypeInfo { Name: Declaration.RuntimeContract.Owned };

        // `steal` on a record is a no-op — records are value-typed and have no
        // ownership to transfer. Returning the operand type as-is lets stdlib
        // patterns like `return steal result` keep working when `result` is a
        // record (e.g. Text after the refcounted-record migration).
        bool isRecord = operandType is RecordTypeInfo;

        if (!isOwned && !isRecord && !IsRawEntityType(type: operandType))
        {
            ReportError(code: SemanticDiagnosticCode.StealScopeBoundToken,
                message: $"Cannot steal '{operandType.Name}' - only raw entities and T can be stolen.",
                location: steal.Location);
            steal.ResolvedType = operandType;
            return operandType;
        }

        // #11: Deadref tracking — mark the stolen variable as invalidated
        if (steal.Operand is IdentifierExpression stolenId)
        {
            _deadrefVariables.Add(item: stolenId.Name);
        }

        // `steal T` unwraps to bare T — T is a binding-only ownership marker,
        // and steal transfers the bare entity out of that binding. For raw entity operands
        // (already bare), steal is a no-op on the type.
        // `steal` always produces an rvalue `T` — it consumes an lvalue binding and yields
        // an in-flight entity that must be re-bound (or consumed) at the use site.
        steal.IsInFlight = true;
        if (isOwned && operandType is WrapperTypeInfo { InnerType: not null } owned)
        {
            steal.ResolvedType = owned.InnerType;
            return owned.InnerType;
        }

        steal.ResolvedType = operandType;
        return operandType;
    }

    /// <summary>
    /// Rejects a <c>steal</c> whose operand is an unstealable wrapper: a scope-bound token
    /// (Viewing/Modifying), the internal <c>Hijacked[T]</c>, or a single-threaded reference-counted
    /// handle (Retained/Tracked, which is SHARED ownership). Reports the matching diagnostic and
    /// returns true on any hit; false when the operand is not one of these wrappers.
    /// </summary>
    private bool TryRejectUnstealableWrapper(StealExpression steal, TypeSymbol operandType)
    {
        // Check if the type is a scope-bound wrapper (cannot be stolen)
        if (IsMemoryToken(type: operandType))
        {
            string tokenKind = GetMemoryTokenKind(type: operandType);
            ReportError(code: SemanticDiagnosticCode.StealScopeBoundToken,
                message: $"Cannot steal '{tokenKind}' - scope-bound wrappers cannot be stolen. " +
                         $"Only raw entities can be stolen.",
                location: steal.Location);
            return true;
        }

        // Check for Hijacked[T] (internal ownership, not for user code)
        if (IsHijacked(type: operandType))
        {
            ReportError(code: SemanticDiagnosticCode.StealHijacked,
                message: "Cannot steal 'Hijacked[T]' - internal ownership type cannot be stolen.",
                location: steal.Location);
            return true;
        }

        // Check for a single-threaded reference-counted handle (Retained/Tracked). These are SHARED
        // ownership, not unique — multiple handles to the same non-atomic control block can coexist,
        // so `steal` (an exclusive-transfer marker) is a category error: moving one handle proves
        // nothing about the others. Clone with `.retain()`/`.track()`, or convert to `Guarded`/
        // `Witnessed` (atomic Arc) to move ownership across a coroutine/thread boundary.
        if (operandType.BareName is
            Declaration.RuntimeContract.Retained or Declaration.RuntimeContract.Tracked)
        {
            ReportError(code: SemanticDiagnosticCode.StealSharedOwnership,
                message:
                $"Cannot steal '{operandType.Name}' - a reference-counted handle is shared ownership, " +
                "not unique, so it cannot be exclusively moved. Copy it with `.assign()`, " +
                "or use `Guarded`/`Witnessed` to move ownership across a coroutine/thread boundary.",
                location: steal.Location);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Checks if a type is a scope-bound wrapper (Viewing, Modifying).
    /// Scope-bound wrappers cannot be stolen.
    /// </summary>
    private static bool IsMemoryToken(TypeSymbol type)
    {
        return type.Name is Declaration.RuntimeContract.Viewing or Declaration.RuntimeContract.Modifying;
    }

    /// <summary>
    /// Gets the kind of scope-bound wrapper for error messages.
    /// </summary>
    private static string GetMemoryTokenKind(TypeSymbol type)
    {
        if (type.Name.StartsWith(value: Declaration.RuntimeContract.Viewing))
        {
            return "Viewing[T]";
        }

        if (type.Name.StartsWith(value: Declaration.RuntimeContract.Modifying))
        {
            return "Modifying[T]";
        }

        return type.Name;
    }

    /// <summary>
    /// Checks if a type is Hijacked[T] (internal ownership type).
    /// </summary>
    private static bool IsHijacked(TypeSymbol type)
    {
        return type.Name == Declaration.RuntimeContract.Hijacked;
    }

    /// <summary>
    /// Analyzes a backindex expression (^n = index from end).
    /// Validates that the operand is a non-negative integer type.
    /// </summary>
    /// <param name="back">The back index expression to analyze.</param>
    /// <returns>The BackIndex type.</returns>
    /// <remarks>
    /// BackIndex expressions create indices that count from the end of a sequence:
    /// - ^1 = last element
    /// - ^2 = second to last element
    /// - ^0 = one past the end (valid for slicing, not indexing)
    ///
    /// Used with IndexExpression for end-relative indexing: list[^1], text[^3]
    /// </remarks>
    private TypeSymbol AnalyzeBackIndexExpression(BackIndexExpression back)
    {
        // `^n` is pure sugar for the forward position `count - n`; it carries no runtime type of its
        // own. Analyze the offset as U64 and report the expression's type as U64 (its lowered value).
        // OperatorLoweringPass rewrites the enclosing subscript/slice to the free routine
        // `back_resolve(count:, offset:)`; the `BackIndexExpression` node is the only signal it needs.
        TypeSymbol? u64Type = _registry.LookupType(name: "U64");
        TypeSymbol operandType =
            AnalyzeExpression(expression: back.Operand, expectedType: u64Type);

        if (!IsIntegerType(type: operandType))
        {
            ReportError(code: SemanticDiagnosticCode.BackIndexRequiresInteger,
                message:
                $"BackIndex operator '^' requires an integer operand, got '{operandType.Name}'.",
                location: back.Location);
        }

        return u64Type ?? operandType;
    }

    /// <summary>
    /// Creates a RoutineTypeInfo from a RoutineInfo for first-class routine references.
    /// </summary>
    /// <param name="routine">The routine to create a type for.</param>
    /// <returns>The routine type representing this routine's signature.</returns>
    private RoutineTypeInfo GetRoutineType(RoutineInfo routine)
    {
        // Extract parameter types from ParameterInfo
        var parameterTypes = routine.Parameters
                                    .Select(selector: p => p.Type)
                                    .ToList();

        // Get return type (null means None/void)
        TypeSymbol? returnType = routine.ReturnType;

        // Create or retrieve the cached routine type
        return _registry.GetOrCreateRoutineType(parameterTypes: parameterTypes,
            returnType: returnType,
            isFailable: routine.IsFailable);
    }
}
