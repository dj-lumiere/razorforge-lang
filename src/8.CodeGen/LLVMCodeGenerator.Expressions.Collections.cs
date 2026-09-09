using System.Text;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Compiler.CodeGen;

/// <summary>
/// Expression code generation for collection literals and variadic argument packing.
/// Array[T,N] and BitArray[N] are emitted as inline insertvalue sequences here.
/// All other collection literals from [] / {} / {} syntax must be lowered by
/// ExpressionLoweringPass to CreatorExpression + add calls before reaching codegen.
/// The CollectionConstruction lowering kind (from explicit List(...) calls) still routes
/// through EmitCollectionLiteralConstructor for non-literal construction.
/// </summary>
public partial class LlvmCodeGenerator
{
    private static TypeInfo UnwrapCollectionStorageType(TypeInfo type)
    {
        TypeInfo current = type;
        while (current is WrapperTypeInfo { Name: Declaration.RuntimeContract.Owned } wrapper)
        {
            current = wrapper.InnerType;
        }

        return current;
    }

    /// <summary>
    /// Returns true if the type is Array[T,N] or BitArray[N] — the only list-literal types
    /// that remain in codegen as inline IR (insertvalue). All other collection types must be
    /// lowered to CreatorExpression + add calls by ExpressionLoweringPass.
    /// </summary>
    private static bool IsArrayOrBitArrayLiteral(TypeInfo? type)
    {
        if (type == null) return false;
        TypeInfo concrete = UnwrapCollectionStorageType(type);
        string baseName = GetGenericBaseName(type: concrete) ?? concrete.Name;
        return baseName is "Array" or "BitArray";
    }

    /// <summary>
    /// Emits an Array[T,N] or BitArray[N] literal using inline insertvalue IR.
    /// Guarded at the call site — only reached when IsArrayOrBitArrayLiteral is true.
    /// </summary>
    private string EmitListLiteral(StringBuilder sb, ListLiteralExpression list)
    {
        TypeInfo concreteListType = UnwrapCollectionStorageType(list.ResolvedType!);
        return EmitCollectionLiteralConstructor(sb: sb, resolvedType: concreteListType,
            arguments: list.Elements);
    }

    private static readonly string[] OwnedCollectionBaseNames =
    {
        "List", "Dict", "Set", "CircularList", "SortedDict", "SortedSet", "SortedList",
        "SecureDict", "SecureSet", "BitList", "PriorityQueue"
    };

    /// <summary>
    /// Resolves an owned collection parameter type to its concrete collection type for emitting a
    /// collection-literal default. Unwraps the transparent <c>Owned</c> wrapper; a borrow or
    /// reference-counting wrapper (Accessing/Viewing/Modifying/Controlling/Retained/Tracked) is NOT
    /// plainly owned and returns false — for those we do not inline-construct a default (the callee
    /// would not free it, so a temporary would leak). Returns true with <paramref name="collType"/>
    /// set for a plainly-owned collection.
    /// </summary>
    private static bool TryGetOwnedCollectionType(TypeInfo? paramType, out TypeInfo collType)
    {
        collType = null!;
        if (paramType == null) return false;
        TypeInfo t = paramType;
        while (t is WrapperTypeInfo wrapper)
        {
            if (wrapper.Name == Declaration.RuntimeContract.Owned) { t = wrapper.InnerType; continue; }
            return false;
        }
        string baseName = GetGenericBaseName(type: t) ?? t.Name;
        if (Array.IndexOf(array: OwnedCollectionBaseNames, value: baseName) >= 0)
        {
            collType = t;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Emits a parameter default value that is an EMPTY collection literal (<c>{:}</c> / <c>[]</c> /
    /// <c>{}</c>) on a plainly-owned collection parameter, returning the created collection register.
    /// Collection literals are normally lowered by ExpressionLoweringPass, but default values are
    /// never lowered — so we construct an empty collection inline here (create, no adds). Because the
    /// parameter is owned, the callee frees it via its own destroy, so there is no caller-side
    /// teardown gap. Returns false (caller falls back to EmitExpression) for non-empty literals or
    /// non-owned params. Non-empty collection defaults remain unsupported (the element expressions are
    /// never SA-analyzed, so they lack a ResolvedType for inline emission).
    /// </summary>
    private bool TryEmitEmptyCollectionDefault(StringBuilder sb, TypeInfo? paramType,
        Expression? defaultValue, out string value)
    {
        value = "";
        bool isEmptyLiteral = defaultValue is ListLiteralExpression { Elements.Count: 0 }
            or SetLiteralExpression { Elements.Count: 0 }
            or DictLiteralExpression { Pairs.Count: 0 };
        if (!isEmptyLiteral) return false;
        if (!TryGetOwnedCollectionType(paramType: paramType, out TypeInfo collType)) return false;
        value = EmitCollectionCreate(sb: sb, resolvedType: collType);
        return true;
    }

    /// <summary>
    /// Emits a collection literal constructor: Array[T,N] (insertvalue), BitArray[N] (bit packing),
    /// or entity collection (create + add calls).
    /// Called from EmitListLiteral (Array/BitArray only) and from the CollectionConstruction
    /// lowering kind path in EmitRoutineCall / EmitMemberRoutineCall.
    /// </summary>
    private string EmitCollectionLiteralConstructor(StringBuilder sb, TypeInfo resolvedType,
        List<Expression> arguments)
    {
        string typeName = resolvedType.Name;
        string baseName = GetGenericBaseName(type: resolvedType) ?? typeName;

        switch (baseName)
        {
            // ACCEPTED codegen boundary case (not debt): Array[T,N] / BitArray[N] are fixed-size
            // backend-native aggregates with no heap allocation and no stdlib create() body — they are
            // pure `insertvalue` (element-per-slot for Array, LSB-packed bytes for BitArray). Building
            // the constant aggregate is intrinsically a codegen concern, so these stay inline. The
            // BitArray literal bit-packing shares PackBitArrayLiteralBytes with the preset path (D6).

            // Array[T, N]: inline array construction via insertvalue
            case "Array":
                return EmitArrayLiteralInline(sb: sb, resolvedType: resolvedType,
                    arguments: arguments);
            // BitArray[N]: inline bit-packed array construction. All-literal elements pack at
            // compile time via the shared PackBitArrayLiteralBytes; a non-literal element falls
            // back to the runtime bit-pack.
            case "BitArray":
                return EmitBitArrayLiteralInline(sb: sb, resolvedType: resolvedType,
                    arguments: arguments);
        }

        // Entity collections (List, Set, Dict, etc.): create() + add/add_last calls.
        // Reached only via CollectionConstruction lowering kind, not from ListLiteralExpression
        // (which is lowered by ExpressionLoweringPass for entity collection types).
        string collectionPtr =
            EmitCollectionCreate(sb: sb, resolvedType: resolvedType);

        string addMemberRoutineName;
        bool isMapType = baseName is "Dict" or "SortedDict" or "SecureDict";
        bool isSequenceType = baseName is "List" or "CircularList" or "BitList";
        addMemberRoutineName = isSequenceType
            ? Declaration.RuntimeContract.Collection.AddLast
            : Declaration.RuntimeContract.Collection.Add;

        ResolvedMemberRoutine? resolvedAdd = ResolveMemberRoutine(receiverType: resolvedType, memberRoutineName: addMemberRoutineName);
        if (resolvedAdd == null) return collectionPtr;

        string mangledAdd = resolvedAdd.MangledName;

        if (isMapType)
        {
            EmitMapCollectionAdds(sb: sb, arguments: arguments, collectionPtr: collectionPtr,
                mangledAdd: mangledAdd);
        }
        else
        {
            EmitSequenceCollectionAdds(sb: sb, arguments: arguments, baseName: baseName,
                collectionPtr: collectionPtr, mangledAdd: mangledAdd, resolvedAdd: resolvedAdd);
        }

        return collectionPtr;
    }

    /// <summary>
    /// Array[T, N] literal: inline array construction via a per-element insertvalue chain.
    /// </summary>
    private string EmitArrayLiteralInline(StringBuilder sb, TypeInfo resolvedType,
        List<Expression> arguments)
    {
        string llvmType = GetLlvmType(type: resolvedType);
        string current = "zeroinitializer";
        for (int i = 0; i < arguments.Count; i++)
        {
            string elemVal = EmitExpression(sb: sb, expr: arguments[index: i]);
            TypeInfo? elemType = GetExpressionType(expr: arguments[index: i]);
            string elemLlvm = elemType != null ? GetLlvmType(type: elemType) : "i64";
            string next = NextTemp();
            EmitLine(sb: sb,
                line:
                $"  {next} = insertvalue {llvmType} {current}, {elemLlvm} {elemVal}, {i}");
            current = next;
        }

        return current;
    }

    /// <summary>
    /// BitArray[N] literal: inline bit-packed construction. All-literal elements pack at compile time
    /// via the shared PackBitArrayLiteralBytes; a non-literal element falls back to the runtime bit-pack.
    /// </summary>
    private string EmitBitArrayLiteralInline(StringBuilder sb, TypeInfo resolvedType,
        List<Expression> arguments)
    {
        int[] bytes = PackBitArrayLiteralBytes(elements: arguments, out bool allLiteral);
        if (!allLiteral)
            return EmitBitArrayRuntime(sb: sb, resolvedType: resolvedType,
                arguments: arguments);

        string llvmType = GetLlvmType(type: resolvedType);
        string current = "zeroinitializer";
        for (int byteIdx = 0; byteIdx < bytes.Length; byteIdx++)
        {
            string next = NextTemp();
            EmitLine(sb: sb,
                line: $"  {next} = insertvalue {llvmType} {current}, i8 {bytes[byteIdx]}, {byteIdx}");
            current = next;
        }

        return current;
    }

    /// <summary>
    /// Emits the key/value add calls for a map collection literal (Dict / SortedDict / SecureDict).
    /// </summary>
    private void EmitMapCollectionAdds(StringBuilder sb, List<Expression> arguments,
        string collectionPtr, string mangledAdd)
    {
        foreach (Expression arg in arguments)
        {
            if (arg is not DictEntryLiteralExpression entry)
            {
                continue;
            }

            string keyVal = EmitExpression(sb: sb, expr: entry.Key);
            string valVal = EmitExpression(sb: sb, expr: entry.Value);
            TypeInfo? keyType = GetExpressionType(expr: entry.Key);
            TypeInfo? valueType = GetExpressionType(expr: entry.Value);
            string keyLlvm = keyType != null ? GetLlvmType(type: keyType) : "i64";
            string valLlvm = valueType != null ? GetLlvmType(type: valueType) : "i64";

            if (!_generatedRoutines.Contains(item: mangledAdd))
            {
                _rfRoutineDeclarations[key: mangledAdd] =
                    $"declare i1 @{mangledAdd}(ptr, {keyLlvm}, {valLlvm})";
                _generatedRoutines.Add(item: mangledAdd);
            }

            EmitLine(sb: sb,
                line:
                $"  call i1 @{mangledAdd}(ptr {collectionPtr}, {keyLlvm} {keyVal}, {valLlvm} {valVal})");
        }
    }

    /// <summary>
    /// Emits the element add calls for a sequence/set collection literal (List/CircularList/BitList/Set/...).
    /// </summary>
    private void EmitSequenceCollectionAdds(StringBuilder sb, List<Expression> arguments,
        string baseName, string collectionPtr, string mangledAdd, ResolvedMemberRoutine resolvedAdd)
    {
        foreach (Expression arg in arguments)
        {
            string elemVal = EmitExpression(sb: sb, expr: arg);
            TypeInfo? elemType = GetExpressionType(expr: arg);
            string elemLlvm = elemType != null ? GetLlvmType(type: elemType) : "i64";

            if (!_generatedRoutines.Contains(item: mangledAdd))
            {
                string retType = baseName is "Set" or "SortedSet" or "SecureSet" ? "i1" : "void";
                _rfRoutineDeclarations[key: mangledAdd] =
                    $"declare {retType} @{mangledAdd}(ptr, {elemLlvm})";
                _generatedRoutines.Add(item: mangledAdd);
            }

            bool returnsVoid = resolvedAdd.Routine.ReturnType == null ||
                               resolvedAdd.Routine.ReturnType.Name == "None";
            if (returnsVoid)
            {
                EmitLine(sb: sb,
                    line: $"  call void @{mangledAdd}(ptr {collectionPtr}, {elemLlvm} {elemVal})");
            }
            else
            {
                string discarded = NextTemp();
                EmitLine(sb: sb,
                    line:
                    $"  {discarded} = call i1 @{mangledAdd}(ptr {collectionPtr}, {elemLlvm} {elemVal})");
            }
        }
    }

    /// <summary>
    /// Runtime fallback for BitArray construction when arguments are non-literal booleans.
    /// </summary>
    private string EmitBitArrayRuntime(StringBuilder sb, TypeInfo resolvedType,
        List<Expression> arguments)
    {
        string llvmType = GetLlvmType(type: resolvedType);
        int bitCount = arguments.Count;
        int byteCount = (bitCount + 7) / 8;

        string current = "zeroinitializer";
        for (int byteIdx = 0; byteIdx < byteCount; byteIdx++)
        {
            string byteAccum = "0";
            for (int bitIdx = 0; bitIdx < 8 && byteIdx * 8 + bitIdx < bitCount; bitIdx++)
            {
                string boolVal =
                    EmitExpression(sb: sb, expr: arguments[index: byteIdx * 8 + bitIdx]);
                string extended = NextTemp();
                EmitLine(sb: sb, line: $"  {extended} = zext i1 {boolVal} to i8");
                if (bitIdx > 0)
                {
                    string shifted = NextTemp();
                    EmitLine(sb: sb, line: $"  {shifted} = shl i8 {extended}, {bitIdx}");
                    extended = shifted;
                }

                string ored = NextTemp();
                EmitLine(sb: sb, line: $"  {ored} = or i8 {byteAccum}, {extended}");
                byteAccum = ored;
            }

            string next = NextTemp();
            EmitLine(sb: sb,
                line: $"  {next} = insertvalue {llvmType} {current}, i8 {byteAccum}, {byteIdx}");
            current = next;
        }

        return current;
    }

    /// <summary>
    /// Emits a zero-arg create() call for a collection type, handling monomorphization.
    /// </summary>
    private string EmitCollectionCreate(StringBuilder sb, TypeInfo? resolvedType)
    {
        if (resolvedType == null) return "null";

        ResolvedMemberRoutine? resolved = resolvedType.IsGenericResolution
            ? null
            : ResolveMemberRoutine(receiverType: resolvedType, memberRoutineName: RoutineInfo.CreatorName);

        if (resolved is { Routine.Parameters.Count: > 0 })
            resolved = null;

        if (resolved == null)
        {
            RoutineInfo? creator = LookupCollectionZeroArgCreator(resolvedType: resolvedType);

            if (creator != null)
            {
                // Mangle through MangleRoutineName so the call symbol matches the decorated define
                // ([member] Module.Type.create(...)). For a generic resolution the looked-up
                // `creator` is keyed on the generic-def owner (List[T]); substitute the concrete owner
                // (List[S64]) first so the symbol is the concrete one the GMP define emits.
                RoutineInfo mangleCreator = resolvedType.IsGenericResolution
                    ? _registry.SubstituteMemberRoutineForOwner(memberRoutine: creator, resolvedOwner: resolvedType) ?? creator
                    : creator;
                string funcName = MangleRoutineName(routine: mangleCreator);

                if (!_generatedRoutines.Contains(item: funcName))
                    GenerateRoutineDeclaration(routine: creator, nameOverride: funcName);

                string result = NextTemp();
                EmitLine(sb: sb, line: $"  {result} = call ptr @{funcName}()");
                return result;
            }
        }
        else
        {
            string funcName = resolved.MangledName;
            if (!_generatedRoutines.Contains(item: funcName))
                GenerateRoutineDeclaration(routine: resolved.Routine, nameOverride: funcName);

            string result = NextTemp();
            EmitLine(sb: sb, line: $"  {result} = call ptr @{funcName}()");
            return result;
        }

        throw new InvalidOperationException(
            $"No 'create' routine found for collection type '{resolvedType.Name}'. " +
            "All collection types must have a registered 'create' body in the stdlib.");
    }

    /// <summary>
    /// Looks up the zero-arg <c>create</c> routine for a collection type: the concrete owner's
    /// overload first, then the generic-definition owner's. Returns null when no zero-arg creator
    /// exists (a creator with parameters is rejected).
    /// </summary>
    private RoutineInfo? LookupCollectionZeroArgCreator(TypeInfo resolvedType)
    {
        string createName = $"{resolvedType.FullName}.create";
        RoutineInfo? creator =
            _registry.LookupRoutineOverload(baseName: createName, argTypes: new List<TypeInfo>());
        if (creator is { Parameters.Count: > 0 })
            creator = null;

        if (creator == null)
        {
            TypeInfo? genericDef = resolvedType switch
            {
                EntityTypeInfo { GenericDefinition: not null } e => e.GenericDefinition,
                RecordTypeInfo { GenericDefinition: not null } r => r.GenericDefinition,
                _ => null
            };
            if (genericDef != null)
            {
                string genCreateName = $"{RoutineInfo.GetTypeIdentity(type: genericDef)}.create";
                // Signature-only: the 0-arg creator matches the empty-argTypes overload; no name-only fallback.
                creator = _registry.LookupRoutineOverload(baseName: genCreateName,
                    argTypes: new List<TypeInfo>());
                if (creator is { Parameters.Count: > 0 })
                    creator = null;
            }
        }

        return creator;
    }
}
