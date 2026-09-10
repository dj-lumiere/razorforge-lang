using Compiler.Diagnostics;
using Compiler.Tokenizer;
using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;
using Compiler.Verification.Enums;

namespace Compiler.Verification;

using TypeSymbol = TypeInfo;

public sealed partial class SemanticVerifier
{
    private static TypeSymbol UnwrapCollectionLiteralExpectedType(TypeSymbol type)
    {
        TypeSymbol current = type;
        while (true)
        {
            if (current is WrapperTypeInfo wrapper)
            {
                current = wrapper.InnerType;
                continue;
            }

            // T / Retained[T] / Tracked[T] / Roamed[T] are declared as `record` in stdlib so they
            // surface as RecordTypeInfo, not WrapperTypeInfo. Their single TypeArgument is the
            // wrapped collection type — unwrap so the literal can resolve its base name
            // (PriorityQueue, SortedSet, etc.) from the expected type even when LHS is
            // `Owned[SortedSet[S64]]` etc. Use base-name extraction since instantiated record
            // types have Name like "Foo", not bare "Owned". Roamed[T] is the Suflae lowering of an
            // annotated collection type (`var l: List[S64]` ⇒ `Roamed[List[S64]]`); it MUST be
            // transparent here too, else a bare literal element loses its S64 context and falls back
            // to the Suflae `Integer` default — producing `List[Integer]` that won't assign to the
            // `Roamed[List[S64]]` slot (RF-S201). Inferring the element type through the wrapper is
            // exactly the compiler's job.
            if (current is RecordTypeInfo { TypeArguments: { Count: 1 } recArgs } recRT &&
                GetTypeBaseName(type: recRT) is Declaration.RuntimeContract.Owned
                    or Declaration.RuntimeContract.Retained or Declaration.RuntimeContract.Tracked
                    or Declaration.RuntimeContract.Roamed)
            {
                current = recArgs[index: 0];
                continue;
            }

            break;
        }

        return current;
    }

    /// <summary>
    /// Wraps an entity-typed collection literal in <c>Owned[…]</c> when used in a binding
    /// position (var declaration, field initializer, assignment target). For rvalue
    /// positions (function-call args, expression results), the literal stays as the bare
    /// entity — the caller takes responsibility for its lifetime, and display routines
    /// (`show`/`alert`) see the inner type so `alert([1,2,3])` prints
    /// <c>List(count: 3, …)</c> instead of <c>Owned(addr: …, List(…))</c>.
    ///
    /// `wrapForBinding` controls the behavior: literal-analysis sites that pass `false`
    /// (default — rvalue context) get the bare entity; var-decl / field-init sites pass
    /// `true` so the result is Owned-wrapped and can satisfy the entity-ownership rule
    /// (S413). Switching between contexts is purely a type-annotation thing — codegen
    /// emits the same `List.create + add_last` sequence either way; the Owned wrapper is
    /// `@llvm("ptr")` and shares the entity's pointer.
    /// </summary>
    /// <summary>
    /// Picks the collection literal's own type from an expected (annotation) type. A Suflae entity slot
    /// pins the annotation as <c>Roamed[Core.List[T]]</c>, but the literal itself must stay the BARE
    /// collection — it builds a fresh <c>create + add_last</c> sequence and the SF binding lowering roams
    /// the result at the var/field/assignment site (exactly like a <c>List[T]()</c> constructor RHS).
    /// Stamping <c>Roamed</c> onto the literal would make its <c>add</c>/<c>add_last</c> calls target the
    /// Roamed handle, which has no collection memberRoutine → codegen "no resolved member routine" (or, once the temp is
    /// created as <c>Roamed[Set]()</c>, an uninitialized controller → AccessViolation). Other expected
    /// wrappers (Owned/Retained/Tracked) and exact collection types pass through unchanged.
    /// </summary>
    private static TypeSymbol LiteralTypeFromExpected(TypeSymbol expectedType,
        TypeSymbol? collectionExpectedType)
    {
        return GetTypeBaseName(type: expectedType) == Declaration.RuntimeContract.Roamed
            ? collectionExpectedType!
            : expectedType;
    }

    private TypeSymbol WrapOwnedCollectionLiteralType(TypeSymbol type, bool wrapForBinding = false)
    {
        if (!wrapForBinding)
        {
            return type;
        }

        return type is EntityTypeInfo
            ? _registry.GetOrCreateWrapperType(wrapperName: Declaration.RuntimeContract.Owned,
                innerType: type,
                isReadOnly: false)
            : type;
    }

    private static long? GetConstGenericLong(TypeSymbol? type)
    {
        return type is ConstGenericValueTypeInfo constVal
            ? constVal.Value
            : null;
    }

    private static string GetTypeBaseName(TypeSymbol type)
    {
        return type.BareName;
    }

    private TypeSymbol AnalyzeListLiteralExpression(ListLiteralExpression list,
        TypeSymbol? expectedType = null)
    {
        // Collection literals are entity rvalues — value-in-flight produced by a fresh
        // `create + add_last` sequence. Mark for the auto-bind rule (rvalue T → bound T).
        list.IsInFlight = true;
        // Extract expected element type from list-shaped expected types.
        TypeSymbol? collectionExpectedType = expectedType != null
            ? UnwrapCollectionLiteralExpectedType(type: expectedType)
            : null;
        string? expectedBaseName = collectionExpectedType != null
            ? GetTypeBaseName(type: collectionExpectedType)
            : null;
        TypeSymbol? expectedElementType = ResolveListExpectedElementType(
            collectionExpectedType: collectionExpectedType,
            expectedBaseName: expectedBaseName);

        TypeSymbol? elementType = ResolveListElementType(list: list,
            expectedElementType: expectedElementType);

        ValidateListFixedArity(list: list,
            expectedBaseName: expectedBaseName,
            collectionExpectedType: collectionExpectedType);

        TypeSymbol resultType = ResolveListResultType(expectedType: expectedType,
            expectedBaseName: expectedBaseName,
            collectionExpectedType: collectionExpectedType,
            elementType: elementType);

        // Resolve the `from_literal` static builder for the (non-inline) collection type, so the lowering
        // emits `Type.from_literal(a, b, c)` and reachability seeds the monomorphized body.
        ResolveLiteralBuilder(literal: list,
            resultType: resultType,
            builderElementType: elementType,
            elementCount: list.Elements.Count);
        return resultType;
    }

    /// <summary>
    /// Determines the expected element type for a list literal from the unwrapped collection expected type
    /// and its base name. Bit-typed collections (<c>BitList</c>/<c>BitArray</c>) force <c>Bool</c>;
    /// sequence types extract their first type argument.
    /// </summary>
    private TypeSymbol? ResolveListExpectedElementType(TypeSymbol? collectionExpectedType,
        string? expectedBaseName)
    {
        if (collectionExpectedType is { IsGenericResolution: true, TypeArguments.Count: >= 1 } &&
            expectedBaseName is "List" or "CircularList" or "SortedList" or CollectionNameArray)
        {
            return collectionExpectedType.TypeArguments[index: 0];
        }

        if (expectedBaseName is "BitList" or CollectionNameBitArray)
        {
            return _registry.LookupType(name: "Bool") ?? ErrorTypeInfo.Instance;
        }

        return null;
    }

    /// <summary>
    /// Infers or validates the element type for a list literal: explicit annotation, inferred from the
    /// first element (with subsequent elements validated), propagated from context, or an error.
    /// </summary>
    private TypeSymbol? ResolveListElementType(ListLiteralExpression list,
        TypeSymbol? expectedElementType)
    {
        if (list.ElementType != null)
        {
            return ResolveType(typeExpr: list.ElementType);
        }

        if (list.Elements.Count > 0)
        {
            // Infer from first element, propagating expected element type.
            TypeSymbol elementType = AnalyzeExpression(expression: list.Elements[index: 0],
                expectedType: expectedElementType);

            // Validate all elements have compatible types.
            // Use inferred element type as context for subsequent elements (e.g., [] in [[1,2], []]).
            TypeSymbol elemExpected = expectedElementType ?? elementType;
            for (int i = 1; i < list.Elements.Count; i++)
            {
                TypeSymbol elemType = AnalyzeExpression(expression: list.Elements[index: i],
                    expectedType: elemExpected);
                if (!IsAssignableTo(source: elemType, target: elementType))
                {
                    ReportError(code: SemanticDiagnosticCode.ListElementTypeMismatch,
                        message:
                        $"List element type mismatch: expected '{elementType.Name}', got '{elemType.Name}'.",
                        location: list.Elements[index: i].Location);
                }
            }

            return elementType;
        }

        if (expectedElementType != null)
        {
            // Empty list with expected type from context — use it.
            return expectedElementType;
        }

        ReportError(code: SemanticDiagnosticCode.EmptyListNoTypeAnnotation,
            message: "Cannot infer element type from empty list literal without type annotation.",
            location: list.Location);
        return ErrorTypeInfo.Instance;
    }

    /// <summary>
    /// Validates that a fixed-arity list literal (<c>Array[T,N]</c> or <c>BitArray[N]</c>) has exactly
    /// the expected number of elements, reporting <see cref="SemanticDiagnosticCode.ArgumentCountMismatch"/>
    /// if not.
    /// </summary>
    private void ValidateListFixedArity(ListLiteralExpression list, string? expectedBaseName,
        TypeSymbol? collectionExpectedType)
    {
        if (expectedBaseName == CollectionNameArray && collectionExpectedType?.TypeArguments is
                { Count: >= 2 })
        {
            long? expectedCount =
                GetConstGenericLong(type: collectionExpectedType.TypeArguments[index: 1]);
            if (expectedCount != null && list.Elements.Count != expectedCount.Value)
            {
                ReportError(code: SemanticDiagnosticCode.ArgumentCountMismatch,
                    message:
                    $"Array[{collectionExpectedType.TypeArguments[index: 0].Name}, {expectedCount.Value}] literal requires exactly {expectedCount.Value} elements, got {list.Elements.Count}.",
                    location: list.Location);
            }
        }

        if (expectedBaseName == CollectionNameBitArray && collectionExpectedType?.TypeArguments is
                { Count: >= 1 })
        {
            long? expectedCount =
                GetConstGenericLong(type: collectionExpectedType.TypeArguments[index: 0]);
            if (expectedCount != null && list.Elements.Count != expectedCount.Value)
            {
                ReportError(code: SemanticDiagnosticCode.ArgumentCountMismatch,
                    message:
                    $"BitArray[{expectedCount.Value}] literal requires exactly {expectedCount.Value} elements, got {list.Elements.Count}.",
                    location: list.Location);
            }
        }
    }

    /// <summary>
    /// Resolves the result type of a list literal: uses the expected type when it matches a known sequence
    /// collection base name, otherwise defaults to <c>List[T]</c>.
    /// </summary>
    private TypeSymbol ResolveListResultType(TypeSymbol? expectedType, string? expectedBaseName,
        TypeSymbol? collectionExpectedType, TypeSymbol? elementType)
    {
        if (expectedType != null && expectedBaseName is "List" or "CircularList" or "SortedList"
                or "BitList" or CollectionNameArray or CollectionNameBitArray)
        {
            return LiteralTypeFromExpected(expectedType: expectedType,
                collectionExpectedType: collectionExpectedType);
        }

        // Return List<T> type by default.
        TypeSymbol? listDef = _registry.LookupType(name: "List");
        if (listDef == null || elementType == null)
        {
            return ErrorTypeInfo.Instance;
        }

        TypeSymbol listType = _registry.GetOrCreateResolution(genericDef: listDef,
            typeArguments: [elementType]);
        return WrapOwnedCollectionLiteralType(type: listType);
    }

    /// <summary>
    /// Resolves and monomorphizes a collection literal's `from_literal[K]` static builder onto the literal
    /// node (<see cref="Expression.ResolvedLiteralBuilder"/>). No-op for the inline `Array`/`BitArray`
    /// literals (pure insertvalue) or a type with no variadic `from_literal`. The element type of the
    /// builder's `Array[E, K]` parameter is <paramref name="builderElementType"/> (T for list/set,
    /// DictEntry[K, V] for dicts).
    /// </summary>
    private void ResolveLiteralBuilder(Expression literal, TypeSymbol resultType,
        TypeSymbol? builderElementType, int elementCount)
    {
        if (builderElementType == null)
        {
            return;
        }

        TypeInfo collectionType = UnwrapCollectionLiteralExpectedType(type: resultType);
        string? baseName = GetTypeBaseName(type: collectionType);
        if (baseName is CollectionNameArray or CollectionNameBitArray or null)
        {
            return;
        }

        RoutineInfo? builder = FindVariadicLiteralBuilder(collectionType: collectionType);
        if (builder == null)
        {
            return;
        }

        TypeSymbol? arrayDef = _registry.LookupType(name: "Array");
        if (arrayDef == null)
        {
            return;
        }

        MonomorphizeLiteralBuilder(literal: literal,
            builder: builder,
            arrayDef: arrayDef,
            builderElementType: builderElementType,
            elementCount: elementCount);
    }

    /// <summary>
    /// Finds the variadic <c>from_literal</c> routine on <paramref name="collectionType"/> by collecting
    /// all member-routine candidates and returning the first one with a variadic parameter.
    /// Returns null when no such routine is registered on the type.
    /// </summary>
    private RoutineInfo? FindVariadicLiteralBuilder(TypeInfo collectionType)
    {
        var candidates = new List<RoutineInfo>();
        _registry.CollectMemberRoutineCandidates(type: collectionType,
            memberRoutineName: LiteralBuilderRoutineName,
            candidates: candidates);
        candidates.AddRange(collection: _registry.GetMemberRoutinesForType(type: collectionType)
                                                 .Where(predicate: m =>
                                                      m.Name == LiteralBuilderRoutineName));
        return candidates.FirstOrDefault(predicate: m =>
            m.Parameters.Any(predicate: p => p.IsVariadicParam));
    }

    /// <summary>
    /// Infers the generic type arguments for a <c>from_literal</c> builder by constructing an
    /// <c>Array[E, K]</c> probe literal and unifying it against the builder's parameter, then
    /// stamps the monomorphized (or bare generic) builder onto <paramref name="literal"/>.
    /// </summary>
    private void MonomorphizeLiteralBuilder(Expression literal, RoutineInfo builder,
        TypeSymbol arrayDef, TypeSymbol builderElementType, int elementCount)
    {
        var arityConst = new ConstGenericValueTypeInfo(literalText: elementCount.ToString(),
            value: elementCount,
            explicitTypeName: "U64");
        TypeSymbol arrayType = _registry.GetOrCreateResolution(genericDef: arrayDef,
            typeArguments: [builderElementType, arityConst]);
        var probe =
            new ListLiteralExpression(Elements: [], ElementType: null, Location: literal.Location)
            {
                ResolvedType = arrayType
            };
        List<TypeInfo>? inferred = InferGenericTypeArguments(genericRoutine: builder,
            arguments: [probe]);
        literal.ResolvedLiteralBuilder = inferred != null
            ? _registry.GetOrCreateRoutineResolution(genericDef: builder,
                typeArguments: inferred) ?? builder
            : builder;
    }

    private const string LiteralBuilderRoutineName = "from_literal";
    private const string CollectionNameArray = "Array";
    private const string CollectionNameBitArray = "BitArray";

    private TypeSymbol AnalyzeSetLiteralExpression(SetLiteralExpression set,
        TypeSymbol? expectedType = null)
    {
        // Collection literals are entity rvalues; see AnalyzeListLiteralExpression.
        set.IsInFlight = true;
        // Extract expected element type from set-shaped expected types.
        TypeSymbol? collectionExpectedType = expectedType != null
            ? UnwrapCollectionLiteralExpectedType(type: expectedType)
            : null;
        string? expectedBaseName = collectionExpectedType != null
            ? GetTypeBaseName(type: collectionExpectedType)
            : null;
        TypeSymbol? expectedElementType = null;
        if (collectionExpectedType is { IsGenericResolution: true, TypeArguments.Count: 1 } &&
            expectedBaseName is "Set" or "SortedSet" or "SecureSet")
        {
            expectedElementType = collectionExpectedType.TypeArguments[index: 0];
        }

        TypeSymbol? elementType = ResolveSetElementType(set: set,
            expectedElementType: expectedElementType);

        // Analyze all elements with the inferred/expected element type.
        foreach (Expression elem in set.Elements)
        {
            AnalyzeExpression(expression: elem, expectedType: expectedElementType ?? elementType);
        }

        TypeSymbol setResult = ResolveSetResultType(expectedType: expectedType,
            expectedBaseName: expectedBaseName,
            collectionExpectedType: collectionExpectedType,
            elementType: elementType);

        ResolveLiteralBuilder(literal: set,
            resultType: setResult,
            builderElementType: elementType,
            elementCount: set.Elements.Count);
        return setResult;
    }

    /// <summary>
    /// Infers the element type for a set literal: explicit annotation, first element, expected-type
    /// propagation for empty sets, or an error.
    /// </summary>
    private TypeSymbol? ResolveSetElementType(SetLiteralExpression set,
        TypeSymbol? expectedElementType)
    {
        if (set.ElementType != null)
        {
            return ResolveType(typeExpr: set.ElementType);
        }

        if (set.Elements.Count > 0)
        {
            return AnalyzeExpression(expression: set.Elements[index: 0],
                expectedType: expectedElementType);
        }

        if (expectedElementType != null)
        {
            // Empty set with expected type from context — use it.
            return expectedElementType;
        }

        ReportError(code: SemanticDiagnosticCode.EmptySetNoTypeAnnotation,
            message: "Cannot infer element type from empty set literal without type annotation.",
            location: set.Location);
        return ErrorTypeInfo.Instance;
    }

    /// <summary>
    /// Resolves the result type of a set literal: uses the expected type when it matches a known set
    /// collection base name, otherwise defaults to <c>Set[T]</c>.
    /// </summary>
    private TypeSymbol ResolveSetResultType(TypeSymbol? expectedType, string? expectedBaseName,
        TypeSymbol? collectionExpectedType, TypeSymbol? elementType)
    {
        if (expectedType != null && expectedBaseName is "Set" or "SortedSet" or "SecureSet")
        {
            return LiteralTypeFromExpected(expectedType: expectedType,
                collectionExpectedType: collectionExpectedType);
        }

        // Return Set<T> type by default.
        TypeSymbol? setDef = _registry.LookupType(name: "Set");
        if (setDef == null || elementType == null)
        {
            return ErrorTypeInfo.Instance;
        }

        TypeSymbol setType = _registry.GetOrCreateResolution(genericDef: setDef,
            typeArguments: [elementType]);
        return WrapOwnedCollectionLiteralType(type: setType);
    }

    private TypeSymbol AnalyzeDictLiteralExpression(DictLiteralExpression dict,
        TypeSymbol? expectedType = null)
    {
        // Collection literals are entity rvalues; see AnalyzeListLiteralExpression.
        dict.IsInFlight = true;
        // Extract expected key/value types from dict-shaped expected types.
        TypeSymbol? expectedKeyType = null;
        TypeSymbol? expectedValueType = null;
        TypeSymbol? collectionExpectedType = expectedType != null
            ? UnwrapCollectionLiteralExpectedType(type: expectedType)
            : null;
        string? expectedBaseName = collectionExpectedType != null
            ? GetTypeBaseName(type: collectionExpectedType)
            : null;
        if (collectionExpectedType is { IsGenericResolution: true, TypeArguments.Count: 2 } &&
            expectedBaseName is "Dict" or "SortedDict" or "PriorityQueue" or "SecureDict")
        {
            expectedKeyType = collectionExpectedType.TypeArguments[index: 0];
            expectedValueType = collectionExpectedType.TypeArguments[index: 1];
        }

        (TypeSymbol? keyType, TypeSymbol? valueType) = InferDictKeyValueTypes(dict: dict,
            expectedKeyType: expectedKeyType,
            expectedValueType: expectedValueType);

        // Analyze all pairs with the inferred/expected key/value types.
        foreach ((Expression Key, Expression Value) pair in dict.Pairs)
        {
            AnalyzeExpression(expression: pair.Key, expectedType: expectedKeyType ?? keyType);
            AnalyzeExpression(expression: pair.Value,
                expectedType: expectedValueType ?? valueType);
        }

        TypeSymbol dictResult;
        if (expectedType != null &&
            expectedBaseName is "Dict" or "SortedDict" or "PriorityQueue" or "SecureDict")
        {
            dictResult = LiteralTypeFromExpected(expectedType: expectedType,
                collectionExpectedType: collectionExpectedType);
        }
        else
        {
            // Return Dict<K, V> type by default.
            TypeSymbol? dictDef = _registry.LookupType(name: "Dict");
            if (dictDef == null || keyType == null || valueType == null)
            {
                return ErrorTypeInfo.Instance;
            }

            TypeSymbol dictType = _registry.GetOrCreateResolution(genericDef: dictDef,
                typeArguments: [keyType, valueType]);
            dictResult = WrapOwnedCollectionLiteralType(type: dictType);
        }

        // Dict literals build `DictEntry[K, V]` values — that's the from_literal element type.
        if (keyType != null && valueType != null && _registry.LookupType(name: "DictEntry") is
                { } dictEntryDef)
        {
            TypeSymbol entryType = _registry.GetOrCreateResolution(genericDef: dictEntryDef,
                typeArguments: [keyType, valueType]);
            ResolveLiteralBuilder(literal: dict,
                resultType: dictResult,
                builderElementType: entryType,
                elementCount: dict.Pairs.Count);
        }

        return dictResult;
    }

    /// <summary>
    /// Determines a dict literal's key/value types: from an explicit annotation, inferred from the first
    /// pair, taken from the expected type for an empty literal, else RF-S (empty, no annotation) with
    /// error types.
    /// </summary>
    private (TypeSymbol? keyType, TypeSymbol? valueType) InferDictKeyValueTypes(
        DictLiteralExpression dict, TypeSymbol? expectedKeyType, TypeSymbol? expectedValueType)
    {
        if (dict is { KeyType: not null, ValueType: not null })
        {
            return (ResolveType(typeExpr: dict.KeyType), ResolveType(typeExpr: dict.ValueType));
        }

        if (dict.Pairs.Count > 0)
        {
            return (
                AnalyzeExpression(expression: dict.Pairs[index: 0].Key,
                    expectedType: expectedKeyType),
                AnalyzeExpression(expression: dict.Pairs[index: 0].Value,
                    expectedType: expectedValueType));
        }

        if (expectedKeyType != null && expectedValueType != null)
        {
            // Empty dict with expected types from context — use them
            return (expectedKeyType, expectedValueType);
        }

        ReportError(code: SemanticDiagnosticCode.EmptyDictNoTypeAnnotation,
            message: "Cannot infer types from empty dict literal without type annotation.",
            location: dict.Location);
        return (ErrorTypeInfo.Instance, ErrorTypeInfo.Instance);
    }

    private TypeSymbol AnalyzeDictEntryLiteralExpression(DictEntryLiteralExpression dictEntry,
        TypeSymbol? expectedType = null)
    {
        // Extract expected key/value types from tuple expected type (used by collection constructors)
        TypeSymbol? expectedKeyType = null;
        TypeSymbol? expectedValueType = null;
        if (expectedType is TupleTypeInfo { ElementTypes.Count: 2 } expectedTuple)
        {
            expectedKeyType = expectedTuple.ElementTypes[index: 0];
            expectedValueType = expectedTuple.ElementTypes[index: 1];
        }

        TypeSymbol keyType =
            AnalyzeExpression(expression: dictEntry.Key, expectedType: expectedKeyType);
        TypeSymbol valueType =
            AnalyzeExpression(expression: dictEntry.Value, expectedType: expectedValueType);

        // Resolve to DictEntry[K, V]
        TypeSymbol? dictEntryDef = _registry.LookupType(name: "DictEntry");
        if (dictEntryDef != null)
        {
            return _registry.GetOrCreateResolution(genericDef: dictEntryDef,
                typeArguments: [keyType, valueType]);
        }

        return ErrorTypeInfo.Instance;
    }

    private TypeSymbol AnalyzeTupleLiteralExpression(TupleLiteralExpression tuple,
        TypeSymbol? expectedType = null)
    {
        // Extract per-element expected types from tuple expected type
        List<TypeInfo>? expectedElementTypes = null;
        if (expectedType is TupleTypeInfo expectedTuple &&
            expectedTuple.ElementTypes.Count == tuple.Elements.Count)
        {
            expectedElementTypes = expectedTuple.ElementTypes;
        }

        // Analyze all element expressions
        var elementTypes = new List<TypeSymbol>();
        for (int i = 0; i < tuple.Elements.Count; i++)
        {
            TypeSymbol? elemExpected = expectedElementTypes?[i];
            TypeSymbol elementType = AnalyzeExpression(expression: tuple.Elements[i],
                expectedType: elemExpected);
            elementTypes.Add(item: elementType);
        }

        // Empty tuples are not allowed - use None instead
        if (elementTypes.Count == 0)
        {
            ReportError(code: SemanticDiagnosticCode.UnknownType,
                message: "Empty tuples are not allowed. Use 'None' for the unit type.",
                location: tuple.Location);
            return ErrorTypeInfo.Instance;
        }

        return _registry.GetOrCreateTupleType(elementTypes: elementTypes);
    }

    private TypeSymbol AnalyzeTypeConversionExpression(TypeConversionExpression conv)
    {
        AnalyzeExpression(expression: conv.Expression);

        TypeSymbol? targetType = LookupTypeWithImports(name: conv.TargetType);
        if (targetType == null)
        {
            ReportError(code: SemanticDiagnosticCode.UnknownConversionTargetType,
                message: $"Unknown conversion target type '{conv.TargetType}'.",
                location: conv.Location);
            return ErrorTypeInfo.Instance;
        }

        conv.LoweringKind = CallLoweringKind.ValueConversion;
        conv.ConstructedType = targetType;
        return targetType;
    }

    private TypeSymbol AnalyzeChainedComparisonExpression(ChainedComparisonExpression chain)
    {
        // Validate that operators don't mix ascending and descending
        ValidateComparisonChain(chain: chain, location: chain.Location);

        // Analyze all operands and validate comparisons between consecutive pairs
        var operandTypes = new List<TypeSymbol>();
        foreach (Expression operand in chain.Operands)
        {
            operandTypes.Add(item: AnalyzeExpression(expression: operand));
        }

        // Bare unsuffixed integer literals default to S64; re-infer them against the chain's concrete
        // integer operand so `0 < n < 100` (n: S32) conforms the literals to S32 instead of tripping
        // RF-S060. A single pivot (the first non-literal fixed-width operand) covers the whole chain,
        // so even `0 < 5 < n` conforms both literals. Mirrors the pairwise literal re-inference in
        // AnalyzeBinaryExpression.
        TypeSymbol? pivot = null;
        for (int i = 0; i < chain.Operands.Count; i++)
        {
            if (!IsUnsuffixedIntegerLiteral(expr: chain.Operands[index: i]) &&
                IsFixedWidthIntegerType(type: operandTypes[index: i]))
            {
                pivot = operandTypes[index: i];
                break;
            }
        }

        if (pivot != null)
        {
            for (int i = 0; i < chain.Operands.Count; i++)
            {
                if (IsUnsuffixedIntegerLiteral(expr: chain.Operands[index: i]) &&
                    operandTypes[index: i].Name != pivot.Name)
                {
                    operandTypes[index: i] = AnalyzeExpression(
                        expression: chain.Operands[index: i],
                        expectedType: pivot);
                }
            }
        }

        // Validate each comparison pair
        for (int i = 0; i < chain.Operators.Count; i++)
        {
            ValidateComparisonOperands(left: operandTypes[index: i],
                right: operandTypes[index: i + 1],
                op: chain.Operators[index: i],
                location: chain.Location);
        }

        // Chained comparisons always return bool
        return _registry.LookupType(name: "Bool") ?? ErrorTypeInfo.Instance;
    }

    /// <summary>
    /// True for a bare (unsuffixed) integer literal — one whose width is still contextually
    /// inferable rather than pinned by a suffix. Such literals default to S64 when analyzed
    /// without an expected type and must be re-inferred against a typed peer.
    /// </summary>
    private static bool IsUnsuffixedIntegerLiteral(Expression expr)
    {
        return expr is LiteralExpression
        {
            LiteralType: TokenType.IntegerLiteral or TokenType.S64Literal
            or TokenType.UndecidedInteger
        };
    }

    private TypeSymbol AnalyzeBlockExpression(BlockExpression block)
    {
        // Block expression evaluates to its contained value expression
        return AnalyzeExpression(expression: block.Value);
    }

    private TypeSymbol AnalyzeWithExpression(WithExpression with)
    {
        TypeSymbol baseType = AnalyzeExpression(expression: with.Base);

        // 'with' expressions are only valid on record types
        if (baseType.Category != TypeCategory.Record)
        {
            ReportError(code: SemanticDiagnosticCode.WithExpressionNotRecord,
                message: $"'with' expression requires a record type, got '{baseType.Name}'.",
                location: with.Location);
        }
        else if (!IsTriviallyAssignable(type: baseType))
        {
            // `with` lowers to `tmp = base.assign(); tmp.field = v` — so the base must obey
            // Assignable. Records with ownership-bearing fields that don't opt in are rejected
            // here rather than producing a broken lowered AST.
            ReportError(code: SemanticDiagnosticCode.WithBaseNotAssignable,
                message:
                $"'with' expression base of type '{baseType.Name}' must obey 'Assignable'. " +
                "Add 'obeys Assignable' and define 'assign() -> Me', or reconstruct the value explicitly.",
                location: with.Location);
        }

        // Analyze update expressions
        foreach ((List<string>? fieldPath, Expression? index, Expression value) in with.Updates)
        {
            // Analyze index expression if present
            if (index != null)
            {
                AnalyzeExpression(expression: index);
            }

            AnalyzeExpression(expression: value);

            if (fieldPath is { Count: > 0 } && baseType is RecordTypeInfo recordType)
            {
                MemberVariableInfo? memberInfo =
                    recordType.LookupMemberVariable(memberVariableName: fieldPath[index: 0]);
                if (memberInfo == null)
                {
                    // The field named in the update doesn't exist on the record.
                    ReportError(code: SemanticDiagnosticCode.MemberVariableNotFound,
                        message:
                        $"'{baseType.Name}' has no member variable '{fieldPath[index: 0]}'.",
                        location: with.Location);
                }
                // #45: Cannot modify secret member variables in 'with' expression
                else if (memberInfo is { Visibility: VisibilityModifier.Secret })
                {
                    ReportError(code: SemanticDiagnosticCode.WithSecretMemberProhibited,
                        message:
                        $"Cannot modify secret member variable '{fieldPath[index: 0]}' in 'with' expression.",
                        location: with.Location);
                }
            }
        }

        // Returns the same type as the base
        return baseType;
    }

    /// <summary>
    /// Analyzes a when expression (pattern matching expression).
    /// Returns the common type of all branch results.
    /// </summary>
    private TypeSymbol AnalyzeWhenExpression(WhenExpression when)
    {
        // Analyze the matched expression (Bool for subject-less when — arms are conditions)
        TypeSymbol matchedType = when.Expression != null
            ? AnalyzeExpression(expression: when.Expression)
            : _registry.LookupType(name: "Bool") ?? ErrorTypeInfo.Instance;

        // #88: Pattern order enforcement — else/wildcard must be last
        ValidateWhenPatternOrder(when: when);

        // #130/#148: Duplicate pattern detection
        ValidateWhenDuplicatePatterns(when: when);

        TypeSymbol? resultType = null;
        bool hasElse = false;

        foreach (WhenClause clause in when.Clauses)
        {
            _registry.EnterScope(kind: ScopeKind.Block, name: "when_clause");

            // Analyze the pattern
            AnalyzePattern(pattern: clause.Pattern, matchedType: matchedType);

            // Check for else clause
            if (clause.Pattern is WildcardPattern or ElsePattern)
            {
                hasElse = true;
            }

            resultType = AnalyzeWhenClauseBody(clause: clause, resultType: resultType);

            _registry.ExitScope();
        }

        // Check exhaustiveness — when expressions MUST produce a value for all inputs
        if (!hasElse)
        {
            ExhaustivenessResult exhaustiveness = CheckExhaustiveness(
                clauses: when.Clauses,
                matchedType: matchedType);

            if (!exhaustiveness.IsExhaustive)
            {
                string missing = exhaustiveness.MissingCases.Count > 0
                    ? $" Missing cases: {string.Join(separator: ", ", values: exhaustiveness.MissingCases)}."
                    : "";
                ReportError(code: SemanticDiagnosticCode.NonExhaustiveMatch,
                    message:
                    $"When expression is not exhaustive — all possible values must be handled.{missing}",
                    location: when.Location);
            }
        }

        return resultType ?? ErrorTypeInfo.Instance;
    }

    /// <summary>
    /// #88: Reports PatternOrderViolation for any clause that follows an <c>else</c>/wildcard pattern
    /// (which must be last).
    /// </summary>
    private void ValidateWhenPatternOrder(WhenExpression when)
    {
        bool seenElse = false;
        foreach (Pattern pattern in when.Clauses.Select(selector: clause => clause.Pattern))
        {
            if (seenElse)
            {
                ReportError(code: SemanticDiagnosticCode.PatternOrderViolation,
                    message: "Unreachable pattern after 'else' or wildcard.",
                    location: pattern.Location);
            }

            if (pattern is ElsePattern or WildcardPattern)
            {
                seenElse = true;
            }
        }
    }

    /// <summary>
    /// #130/#148: Reports DuplicatePattern for any clause whose pattern key repeats an earlier one.
    /// </summary>
    private void ValidateWhenDuplicatePatterns(WhenExpression when)
    {
        var seenPatterns = new HashSet<string>();
        foreach (Pattern pattern in when.Clauses.Select(selector: clause => clause.Pattern))
        {
            string? patternKey = GetPatternKey(pattern: pattern);
            if (patternKey != null && !seenPatterns.Add(item: patternKey))
            {
                ReportError(code: SemanticDiagnosticCode.DuplicatePattern,
                    message: $"Duplicate pattern: {patternKey}.",
                    location: pattern.Location);
            }
        }
    }

    /// <summary>
    /// Analyzes one <c>when</c>-expression clause body — an expression (<c>=&gt;</c>), a return, or a
    /// <c>becomes</c> block — and folds its branch type into the running <paramref name="resultType"/>
    /// (reporting a mismatch against the first branch). Returns the updated result type.
    /// </summary>
    private TypeSymbol? AnalyzeWhenClauseBody(WhenClause clause, TypeSymbol? resultType)
    {
        // When expressions require expression bodies that return values
        // The Body is a Statement, but for expressions it should typically be an ExpressionStatement
        if (clause.Body is ExpressionStatement exprStmt)
        {
            TypeSymbol branchType = AnalyzeExpression(expression: exprStmt.Expression);
            return FoldWhenBranchType(resultType: resultType,
                branchType: branchType,
                errorLocation: clause.Body.Location);
        }

        if (clause.Body is ReturnStatement { Value: not null } ret)
        {
            // Allow return statements in when expressions
            TypeSymbol branchType = AnalyzeExpression(expression: ret.Value);
            return resultType ?? branchType;
        }

        if (clause.Body is BlockStatement block)
        {
            return AnalyzeWhenBlockClauseBody(block: block, resultType: resultType);
        }

        // Analyze as regular statement
        AnalyzeStatement(statement: clause.Body);
        return resultType;
    }

    /// <summary>
    /// Folds a branch type into the running result type for a <c>when</c> expression clause, reporting
    /// <see cref="SemanticDiagnosticCode.WhenBranchTypeMismatch"/> when the branch is incompatible with
    /// the established result type. Returns the (possibly updated) result type.
    /// </summary>
    private TypeSymbol? FoldWhenBranchType(TypeSymbol? resultType, TypeSymbol branchType,
        SourceLocation errorLocation)
    {
        if (resultType == null)
        {
            return branchType;
        }

        if (!IsAssignableTo(source: branchType, target: resultType))
        {
            ReportError(code: SemanticDiagnosticCode.WhenBranchTypeMismatch,
                message:
                $"When expression branches have incompatible types: '{resultType.Name}' and '{branchType.Name}'.",
                location: errorLocation);
        }

        return resultType;
    }

    /// <summary>
    /// Analyzes a block-statement body inside a <c>when</c> expression clause: validates <c>becomes</c>
    /// usage and extracts the result type. A block with only <c>becomes</c> should have used
    /// <c>=&gt;</c> syntax; a block without any <c>becomes</c> is an error.
    /// </summary>
    private TypeSymbol? AnalyzeWhenBlockClauseBody(BlockStatement block, TypeSymbol? resultType)
    {
        BecomesStatement? becomesStmt = null;
        int statementCount = 0;

        foreach (Statement stmt in block.Statements)
        {
            AnalyzeStatement(statement: stmt);
            statementCount++;

            if (stmt is BecomesStatement becomes)
            {
                becomesStmt = becomes;
            }
        }

        if (becomesStmt != null)
        {
            // Found a becomes statement — check if it's a single-statement block.
            if (statementCount == 1)
            {
                // Block contains only 'becomes expr' — should use => syntax instead.
                ReportError(code: SemanticDiagnosticCode.SingleExpressionBranchUsesBecomes,
                    message:
                    "Single-expression when branch should use '=>' syntax instead of block with 'becomes'.",
                    location: becomesStmt.Location);
            }

            // Extract the result type from the becomes expression (already analyzed via AnalyzeStatement).
            TypeSymbol branchType = becomesStmt.Value.ResolvedType ?? ErrorTypeInfo.Instance;
            return FoldWhenBranchType(resultType: resultType,
                branchType: branchType,
                errorLocation: becomesStmt.Location);
        }

        if (statementCount > 0)
        {
            // Multi-statement block without 'becomes' in a when expression.
            ReportError(code: SemanticDiagnosticCode.WhenExpressionBlockMissingBecomes,
                message:
                "Multi-statement block in when expression requires 'becomes' to specify the result value.",
                location: block.Location);
        }

        return resultType;
    }

    /// <summary>
    /// Infers type arguments for a generic routine from call arguments.
    /// Returns the inferred type arguments, or null if inference fails.
    /// </summary>
    private List<TypeInfo>? InferGenericTypeArguments(RoutineInfo genericRoutine,
        List<Expression> arguments, TypeSymbol? expectedType = null)
    {
        if (genericRoutine.GenericParameters == null ||
            genericRoutine.GenericParameters.Count == 0)
        {
            return null;
        }

        var typeArgs = new TypeSymbol?[genericRoutine.GenericParameters.Count];

        // First pass: infer from argument types.
        InferTypeArgsFromArguments(genericRoutine: genericRoutine,
            arguments: arguments,
            typeArgs: typeArgs);

        // Second pass: infer any still-unbound generics from `needs` constraints whose constraining
        // param is now known (e.g. `zip[U, S2](other: Accessing[S2]) needs S2 obeys Iterable[U]` —
        // S2 binds from the argument, then U binds from S2's Iterable conformance).
        InferGenericsFromConstraints(routine: genericRoutine, inferred: typeArgs);

        // Third pass: return-type-directed inference. A type parameter that appears ONLY in the return
        // type (e.g. `roamed_none[T]() -> Roamed[T]`, `default[T]() -> T`) can never bind from the
        // arguments; unify the routine's return type against the call's expected type — the field /
        // parameter / assignment target the result flows into — to fill it. Only used to fill gaps
        // (already-inferred params from the argument pass win).
        if (expectedType is not null && expectedType != ErrorTypeInfo.Instance &&
            genericRoutine.ReturnType is { } returnType)
        {
            InferMemberRoutineTypeArgumentsFromTypes(paramType: returnType,
                argType: expectedType,
                genericParameters: genericRoutine.GenericParameters,
                inferred: typeArgs);
        }

        // All type args must be inferred.
        return typeArgs.Any(predicate: t => t == null)
            ? null
            : typeArgs.Select(selector: t => t!)
                      .ToList();
    }

    /// <summary>
    /// First-pass argument-type inference: resolves each argument's type and unifies it against the
    /// corresponding parameter type to bind generic-parameter slots in <paramref name="typeArgs"/>.
    /// Variadic packed-array arguments reuse their already-resolved <c>Array[T,K]</c> type to preserve
    /// the arity constant <c>K</c>; error-typed arguments are skipped.
    /// </summary>
    private void InferTypeArgsFromArguments(RoutineInfo genericRoutine, List<Expression> arguments,
        TypeSymbol?[] typeArgs)
    {
        // GenericParameters is guaranteed non-null when the caller checks IsGenericDefinition,
        // but the field itself is nullable — guard here so the pass-down is clean.
        if (genericRoutine.GenericParameters is not { } genericParameters)
        {
            return;
        }

        int argCount = Math.Min(val1: genericRoutine.Parameters.Count, val2: arguments.Count);
        for (int i = 0; i < argCount; i++)
        {
            TypeSymbol paramType = genericRoutine.Parameters[index: i].Type;
            Expression argExpr = arguments[index: i] is NamedArgumentExpression na
                ? na.Value
                : arguments[index: i];

            // A variadic call packs its trailing args into an Array[T, K] literal (already analyzed
            // against the Array expected type). Re-analyzing it here without that expected type would
            // default it back to List[T] and lose the arity K, so reuse its resolved Array type.
            TypeSymbol argType = ResolveArgTypeForInference(argExpr: argExpr);
            if (argType == ErrorTypeInfo.Instance)
            {
                continue;
            }

            // Recurse into TypeArguments so const- and type-generics inside a parameterized
            // pattern (e.g. array: Array[Byte, N]) bind from the matching position in argType.
            InferMemberRoutineTypeArgumentsFromTypes(paramType: paramType,
                argType: argType,
                genericParameters: genericParameters,
                inferred: typeArgs);
        }
    }

    /// <summary>
    /// Resolves the effective type of an expression for generic type-argument inference. For a
    /// <c>ListLiteralExpression</c> whose resolved type is already an <c>Array[T,K]</c> (a packed
    /// variadic argument), returns that resolved type directly to preserve the arity constant. All
    /// other expressions are analyzed fresh.
    /// </summary>
    private TypeSymbol ResolveArgTypeForInference(Expression argExpr)
    {
        if (argExpr is ListLiteralExpression { ResolvedType: { } packed } &&
            GetTypeBaseName(type: packed) is "Array")
        {
            return packed;
        }

        return AnalyzeExpression(expression: argExpr);
    }

    /// <summary>
    /// Infers still-unbound memberRoutine generics from the routine's <c>needs</c> constraints. For a
    /// constraint <c>S obeys Proto[..., U, ...]</c> where <c>S</c> is already inferred to a concrete
    /// type, the bound type's actual conformance to <c>Proto</c> supplies the concrete arguments,
    /// which are unified positionally against the constraint's type arguments to fill in <c>U</c>.
    /// Enables element-type inference for source-parameterized adapters (zip/extend/set-ops) whose
    /// element generic no longer appears directly in a parameter type.
    /// </summary>
    private static void InferGenericsFromConstraints(RoutineInfo routine, TypeSymbol?[] inferred)
    {
        if (routine.GenericConstraints is not { Count: > 0 } constraints ||
            routine.GenericParameters is not { Count: > 0 } gp)
        {
            return;
        }

        foreach (GenericConstraintDeclaration constraint in constraints)
        {
            if (constraint.ConstraintTypes is not { Count: > 0 } constraintTypes)
            {
                continue;
            }

            int boundIdx = gp.IndexOf(item: constraint.ParameterName);
            if (boundIdx < 0 || inferred[boundIdx] is not { } boundType)
            {
                continue;
            }

            List<TypeSymbol> conformances = ImplementedProtocolsOf(type: boundType);
            InferFromConstraintProtocols(gp: gp,
                inferred: inferred,
                constraintTypes: constraintTypes,
                conformances: conformances);
        }
    }

    /// <summary>
    /// For each protocol type expression in a constraint's type list, locates the bound type's
    /// conformance to that protocol and unifies the protocol's type-argument positions against the
    /// constraint's type-argument positions to bind still-unresolved generic slots.
    /// </summary>
    private static void InferFromConstraintProtocols(List<string> gp, TypeSymbol?[] inferred,
        List<TypeExpression> constraintTypes, List<TypeSymbol> conformances)
    {
        foreach (TypeExpression ct in constraintTypes)
        {
            if (ct.GenericArguments is not { Count: > 0 } ctArgs)
            {
                continue;
            }

            TypeSymbol? conformance =
                conformances.FirstOrDefault(predicate: p => ProtocolBaseName(type: p) == ct.Name);
            if (conformance?.TypeArguments is not { Count: > 0 } confArgs)
            {
                continue;
            }

            BindConstraintTypeArgs(gp: gp,
                inferred: inferred,
                ctArgs: ctArgs,
                confArgs: confArgs);
        }
    }

    /// <summary>
    /// Positionally unifies a constraint's type-argument names against a concrete conformance's type
    /// arguments, binding any unresolved generic slot whose name appears in the constraint list.
    /// </summary>
    private static void BindConstraintTypeArgs(List<string> gp, TypeSymbol?[] inferred,
        List<TypeExpression> ctArgs, List<TypeSymbol> confArgs)
    {
        int n = Math.Min(val1: ctArgs.Count, val2: confArgs.Count);
        for (int k = 0; k < n; k++)
        {
            int uIdx = gp.IndexOf(item: ctArgs[index: k].Name);
            if (uIdx >= 0 && inferred[uIdx] == null)
            {
                inferred[uIdx] = confArgs[index: k];
            }
        }
    }

    /// <summary>Reads a type's implemented-protocol list across the type kinds that carry one.</summary>
    private static List<TypeSymbol> ImplementedProtocolsOf(TypeSymbol type)
    {
        return type switch
        {
            RecordTypeInfo r => r.ImplementedProtocols.ToList(),
            EntityTypeInfo e => e.ImplementedProtocols.ToList(),
            _ => []
        };
    }

    /// <summary>Base (un-parameterized) name of a possibly-parameterized protocol type.</summary>
    private static string ProtocolBaseName(TypeSymbol type)
    {
        return type.BareName;
    }

    /// <summary>
    /// Infers memberRoutine-level generic type arguments for an already owner-resolved memberRoutine.
    /// </summary>
    private List<TypeInfo>? InferMemberRoutineGenericTypeArguments(
        RoutineInfo genericMemberRoutine, List<Expression> arguments,
        TypeSymbol? receiverType = null)
    {
        if (genericMemberRoutine.GenericParameters == null ||
            genericMemberRoutine.GenericParameters.Count == 0)
        {
            return null;
        }

        var inferred = new TypeSymbol?[genericMemberRoutine.GenericParameters.Count];

        // Receiver-based inference for a member declared on a SPECIALIZED generic instantiation
        // (e.g. `routine List[Agent[V]].gather!()`): unify the memberRoutine's MeType pattern
        // (List[Agent[V]]) against the actual receiver (List[Agent[S64]]) to bind generic params
        // (V) that appear only in the receiver, not in any value parameter.
        if (genericMemberRoutine.MeType is { } mePattern && receiverType != null)
        {
            InferMemberRoutineTypeArgumentsFromTypes(paramType: mePattern,
                argType: receiverType,
                genericParameters: genericMemberRoutine.GenericParameters,
                inferred: inferred);
        }

        int argCount = Math.Min(val1: genericMemberRoutine.Parameters.Count,
            val2: arguments.Count);
        for (int i = 0; i < argCount; i++)
        {
            Expression arg = arguments[index: i] is NamedArgumentExpression named
                ? named.Value
                : arguments[index: i];
            TypeSymbol argType = arg.ResolvedType ?? AnalyzeExpression(expression: arg);
            if (argType == ErrorTypeInfo.Instance)
            {
                continue;
            }

            InferMemberRoutineTypeArgumentsFromTypes(
                paramType: genericMemberRoutine.Parameters[index: i].Type,
                argType: argType,
                genericParameters: genericMemberRoutine.GenericParameters,
                inferred: inferred);
        }

        // Infer still-unbound generics from `needs` constraints (e.g. U from `S obeys Iterable[U]`).
        InferGenericsFromConstraints(routine: genericMemberRoutine, inferred: inferred);

        for (int i = 0; i < inferred.Length; i++)
        {
            if (inferred[i] == null)
            {
                return null;
            }
        }

        return inferred.ToList()!;
    }

    private static void InferMemberRoutineTypeArgumentsFromTypes(TypeSymbol paramType,
        TypeSymbol argType, List<string> genericParameters, TypeSymbol?[] inferred)
    {
        if (paramType is GenericParameterTypeInfo)
        {
            BindInferredSlot(name: paramType.Name,
                argType: argType,
                genericParameters: genericParameters,
                inferred: inferred);
            return;
        }

        // Marker borrow wrappers around a BARE generic param (Accessing[S2]/Controlling[S2]) are
        // transparent at call sites: a bare argument `a` passed where `Accessing[S2]` is expected
        // binds S2 to the WHOLE argument type. Without this, `other: Accessing[S2]` against arg
        // `List[S64]` would wrongly element-wise-bind S2 to S64 (the inner element). Restricted to a
        // bare-generic inner so wrappers around constructed types (e.g. `Accessing[List[T]]`) keep
        // the normal element-wise unification that binds their inner params (T) correctly.
        if (TryInferFromMarkerProtocolWrapper(paramType: paramType,
                argType: argType,
                genericParameters: genericParameters,
                inferred: inferred))
        {
            return;
        }

        if (paramType is { TypeArguments: { Count: > 0 } paramArgs } &&
            argType is { TypeArguments: { Count: > 0 } argArgs } &&
            paramArgs.Count == argArgs.Count)
        {
            for (int i = 0; i < paramArgs.Count; i++)
            {
                InferMemberRoutineTypeArgumentsFromTypes(paramType: paramArgs[index: i],
                    argType: argArgs[index: i],
                    genericParameters: genericParameters,
                    inferred: inferred);
            }
        }

        // RoutineTypeInfo is structural: its parameter/return types live in ParameterTypes/ReturnType,
        // not TypeArguments. Without this branch, `Routine[(T,), U]` would not unify against
        // `Routine[(S64,), S64]` and memberRoutine-level params (e.g. `select[U]`) would stay unresolved.
        InferFromRoutineTypeStructure(paramType: paramType,
            argType: argType,
            genericParameters: genericParameters,
            inferred: inferred);
    }

    /// <summary>
    /// Binds a generic-parameter slot by name if it is not yet bound: sets
    /// <paramref name="inferred"/>[slot] = <paramref name="argType"/> for the first matching slot.
    /// </summary>
    private static void BindInferredSlot(string name, TypeSymbol argType,
        List<string> genericParameters, TypeSymbol?[] inferred)
    {
        int idx = genericParameters.ToList()
                                   .IndexOf(item: name);
        if (idx >= 0 && inferred[idx] == null)
        {
            inferred[idx] = argType;
        }
    }

    /// <summary>
    /// Handles inference through a marker-protocol wrapper (<c>Accessing[S2]</c> /
    /// <c>Controlling[S2]</c>) around a bare generic parameter: binds the wrapper's inner param
    /// slot to the WHOLE argument type rather than element-wise. Returns true and binds the slot
    /// when applicable; returns false otherwise so the caller falls through to element-wise unification.
    /// </summary>
    private static bool TryInferFromMarkerProtocolWrapper(TypeSymbol paramType, TypeSymbol argType,
        List<string> genericParameters, TypeSymbol?[] inferred)
    {
        if (paramType is not { TypeArguments: [GenericParameterTypeInfo markerParam] })
        {
            return false;
        }

        if (!Declaration.RuntimeContract.IsMarkerProtocol(
                baseName: ProtocolBaseName(type: paramType)))
        {
            return false;
        }

        if (ProtocolBaseName(type: argType) is Declaration.RuntimeContract.Accessing
            or Declaration.RuntimeContract.Controlling)
        {
            return false;
        }

        BindInferredSlot(name: markerParam.Name,
            argType: argType,
            genericParameters: genericParameters,
            inferred: inferred);
        return true;
    }

    /// <summary>
    /// Unifies a <c>RoutineTypeInfo</c> parameter against a <c>RoutineTypeInfo</c> argument by
    /// recursively inferring from each paired parameter type and then from the return types.
    /// No-op when either side is not a <c>RoutineTypeInfo</c> or the parameter counts differ.
    /// </summary>
    private static void InferFromRoutineTypeStructure(TypeSymbol paramType, TypeSymbol argType,
        List<string> genericParameters, TypeSymbol?[] inferred)
    {
        if (paramType is not RoutineTypeInfo paramRoutine ||
            argType is not RoutineTypeInfo argRoutine)
        {
            return;
        }

        if (paramRoutine.ParameterTypes.Count != argRoutine.ParameterTypes.Count)
        {
            return;
        }

        for (int i = 0; i < paramRoutine.ParameterTypes.Count; i++)
        {
            InferMemberRoutineTypeArgumentsFromTypes(
                paramType: paramRoutine.ParameterTypes[index: i],
                argType: argRoutine.ParameterTypes[index: i],
                genericParameters: genericParameters,
                inferred: inferred);
        }

        if (paramRoutine.ReturnType is { } paramRet && argRoutine.ReturnType is { } argRet)
        {
            InferMemberRoutineTypeArgumentsFromTypes(paramType: paramRet,
                argType: argRet,
                genericParameters: genericParameters,
                inferred: inferred);
        }
    }
}
