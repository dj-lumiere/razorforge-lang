using System.Text;
using DebugUtils.Repr;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Compiler.CodeGen;

/// <summary>
/// LLVM intrinsic call emission — template-based IR generation for
/// routines annotated with <c>@llvm_ir("...")</c>.
/// </summary>
public partial class LlvmCodeGenerator
{
    /// <summary>
    /// Emits a call to an LLVM intrinsic routine using its <c>@llvm_ir</c> template.
    /// Called from <see cref="EmitRoutineCall"/> and <see cref="EmitMemberRoutineCall"/> when
    /// <c>resolvedRoutine.LlvmIrTemplate != null</c>.
    /// </summary>
    private string EmitLlvmIntrinsicCall(StringBuilder sb, RoutineInfo routine,
        string? receiver, List<Expression> arguments,
        List<TypeExpression>? typeArguments,
        TypeInfo? resolvedReturnType = null)
    {
        // Named arguments may be written out of order; the template substitution and generic
        // inference below bind args to parameters positionally, so reorder into declaration order
        // first (no-op for all-positional or count-mismatched calls).
        arguments = ReorderCallArgsToParamOrder(arguments: arguments, routine: routine);

        // Emit argument values.
        var argValues = new List<string>();
        if (receiver != null)
            argValues.Add(receiver);
        foreach (Expression arg in arguments)
            argValues.Add(EmitExpression(sb: sb, expr: arg));

        // Resolve type arguments to LLVM type strings.
        List<string>? genericParameters =
            routine.GenericParameters ?? routine.GenericDefinition?.GenericParameters;
        var llvmTypeArgs = new List<string>();
        if (typeArguments != null)
        {
            foreach (TypeExpression ta in typeArguments)
                llvmTypeArgs.Add(ResolveTypeExpressionToLlvm(typeExpr: ta));
        }

        List<string> inferredTypeArgs = InferLlvmIntrinsicTypeArguments(routine: routine,
            arguments: arguments,
            resolvedReturnType: resolvedReturnType);

        if (typeArguments == null)
        {
            llvmTypeArgs.AddRange(inferredTypeArgs);
        }
        else if (inferredTypeArgs.Count == llvmTypeArgs.Count)
        {
            for (int i = 0; i < llvmTypeArgs.Count; i++)
            {
                bool unresolvedExplicit = !LooksLikeLlvmType(llvmTypeArgs[i]);
                bool namedGenericMatch =
                    genericParameters is { Count: > 0 } &&
                    i < genericParameters.Count &&
                    llvmTypeArgs[i] == genericParameters[i];
                if (unresolvedExplicit || namedGenericMatch)
                {
                    llvmTypeArgs[i] = inferredTypeArgs[i];
                }
            }
        }

        string mold = routine.LlvmIrTemplate!;
        return EmitFromTemplate(sb: sb, mold: mold, memberRoutine: routine,
            llvmTypeArgs: llvmTypeArgs, args: argValues);
    }

    /// <summary>
    /// Resolves remaining `{expr}` template holes whose contents are arithmetic expressions
    /// over const generic params (e.g. `{(N+7)//8}` for BitArray byte count). Called after
    /// the simpler `{paramName}` substitutions, so anything that's still wrapped in braces
    /// and contains a known param-name token is treated as an arithmetic expression.
    /// </summary>
    private static string ResolveArithmeticHoles(string template, RoutineInfo memberRoutine,
        List<string> llvmTypeArgs)
    {
        if (!template.Contains(value: '{'))
        {
            return template;
        }

        List<string>? genericParameters =
            memberRoutine.GenericParameters ?? memberRoutine.GenericDefinition?.GenericParameters;
        if (genericParameters is not { Count: > 0 })
        {
            return template;
        }

        Dictionary<string, long> paramValues = BuildConstParamValues(
            genericParameters: genericParameters, routineTypeArgs: memberRoutine.TypeArguments,
            llvmTypeArgs: llvmTypeArgs);
        if (paramValues.Count == 0)
        {
            return template;
        }

        var sb = new StringBuilder(capacity: template.Length);
        int pos = 0;
        while (pos < template.Length)
        {
            int open = template.IndexOf(value: '{', startIndex: pos);
            if (open < 0)
            {
                sb.Append(value: template, startIndex: pos, count: template.Length - pos);
                break;
            }
            sb.Append(value: template, startIndex: pos, count: open - pos);
            int close = template.IndexOf(value: '}', startIndex: open + 1);
            if (close < 0)
            {
                sb.Append(value: template, startIndex: open, count: template.Length - open);
                break;
            }
            AppendResolvedHole(sb: sb, template: template, open: open, close: close,
                paramValues: paramValues);
            pos = close + 1;
        }
        return sb.ToString();
    }

    /// <summary>
    /// Builds a param-name → numeric-value map. Prefers the routine's resolved TypeArguments (so
    /// ConstGenericValueTypeInfo.Value is exact), falling back to parsing llvmTypeArgs strings.
    /// </summary>
    private static Dictionary<string, long> BuildConstParamValues(List<string> genericParameters,
        List<TypeInfo>? routineTypeArgs, List<string> llvmTypeArgs)
    {
        var paramValues = new Dictionary<string, long>(comparer: StringComparer.Ordinal);
        for (int i = 0; i < genericParameters.Count; i++)
        {
            if (routineTypeArgs is { } rta && i < rta.Count
                && rta[index: i] is ConstGenericValueTypeInfo constVal)
            {
                paramValues[key: genericParameters[index: i]] = constVal.Value;
                continue;
            }
            if (i < llvmTypeArgs.Count
                && long.TryParse(s: llvmTypeArgs[index: i], result: out long v))
            {
                paramValues[key: genericParameters[index: i]] = v;
            }
        }
        return paramValues;
    }

    /// <summary>
    /// Appends a single template hole to <paramref name="sb"/>: the evaluated const-expression value
    /// when the hole references a known param, or the verbatim <c>{…}</c> text otherwise. Only holes
    /// naming a known param are evaluated (a conservative guard against non-arithmetic braces).
    /// </summary>
    private static void AppendResolvedHole(StringBuilder sb, string template, int open, int close,
        Dictionary<string, long> paramValues)
    {
        string hole = template[(open + 1)..close];
        if (!paramValues.Keys.Any(predicate: p => hole.Contains(value: p)))
        {
            sb.Append(value: template, startIndex: open, count: close - open + 1);
            return;
        }
        try
        {
            long val = RecordTypeInfo.EvaluateConstExprPublic(expr: hole, paramValues: paramValues);
            sb.Append(value: val);
        }
        catch
        {
            sb.Append(value: template, startIndex: open, count: close - open + 1);
        }
    }

    private List<string> InferLlvmIntrinsicTypeArguments(RoutineInfo routine,
        List<Expression> arguments, TypeInfo? resolvedReturnType)
    {
        if (routine.TypeArguments is { Count: > 0 })
        {
            return routine.TypeArguments.Select(selector: GetLlvmIntrinsicTypeArgument).ToList();
        }

        List<string>? genericParameters =
            routine.GenericParameters ?? routine.GenericDefinition?.GenericParameters;
        if (genericParameters is not { Count: > 0 })
        {
            return [];
        }

        var inferred = new Dictionary<string, TypeInfo>(StringComparer.Ordinal);
        var inferredLlvmTypes = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < routine.Parameters.Count && i < arguments.Count; i++)
        {
            TypeInfo? argType = GetExpressionType(expr: arguments[i]);
            if (argType != null)
            {
                InferGenericBindings(pattern: routine.Parameters[i].Type,
                    concrete: argType,
                    inferred: inferred);
            }

            InferGenericLlvmBindings(pattern: routine.Parameters[i].Type,
                concreteLlvmType: GetExpressionLlvmType(expr: arguments[i]),
                inferredLlvmTypes: inferredLlvmTypes);
        }

        if (resolvedReturnType != null && routine.ReturnType != null
            && resolvedReturnType is not GenericParameterTypeInfo)
        {
            InferGenericBindings(pattern: routine.ReturnType,
                concrete: resolvedReturnType,
                inferred: inferred);
            InferGenericLlvmBindings(pattern: routine.ReturnType,
                concreteLlvmType: GetLlvmType(type: resolvedReturnType),
                inferredLlvmTypes: inferredLlvmTypes);
        }

        var llvmTypeArgs = new List<string>(capacity: genericParameters.Count);
        foreach (string genericParam in genericParameters)
        {
            if (inferred.TryGetValue(key: genericParam, value: out TypeInfo? concreteType))
            {
                llvmTypeArgs.Add(GetLlvmIntrinsicTypeArgument(type: concreteType));
                continue;
            }

            if (inferredLlvmTypes.TryGetValue(key: genericParam, value: out string? concreteLlvmType))
            {
                llvmTypeArgs.Add(concreteLlvmType);
                continue;
            }

            return [];
        }

        return llvmTypeArgs;
    }

    private string GetLlvmIntrinsicTypeArgument(TypeInfo type) =>
        type is ConstGenericValueTypeInfo constValue
            ? constValue.Value.ToString()
            : GetLlvmType(type: type);

    private static void InferGenericBindings(TypeInfo pattern, TypeInfo concrete,
        Dictionary<string, TypeInfo> inferred)
    {
        if (pattern is GenericParameterTypeInfo genericParam)
        {
            inferred.TryAdd(key: genericParam.Name, value: concrete);
            return;
        }

        if (pattern is RoutineTypeInfo patternRoutine && concrete is RoutineTypeInfo concreteRoutine)
        {
            InferRoutineBindings(patternRoutine: patternRoutine,
                concreteRoutine: concreteRoutine, inferred: inferred);
            return;
        }

        if (pattern is TupleTypeInfo patternTuple && concrete is TupleTypeInfo concreteTuple)
        {
            InferPairwiseBindings(patterns: patternTuple.ElementTypes,
                concretes: concreteTuple.ElementTypes, inferred: inferred);
            return;
        }

        if (pattern.TypeArguments is { Count: > 0 } patternArgs &&
            concrete.TypeArguments is { Count: > 0 } concreteArgs)
        {
            InferPairwiseBindings(patterns: patternArgs, concretes: concreteArgs,
                inferred: inferred);
        }
    }

    /// <summary>Infers generic bindings from a routine pattern's parameter + return types.</summary>
    private static void InferRoutineBindings(RoutineTypeInfo patternRoutine,
        RoutineTypeInfo concreteRoutine, Dictionary<string, TypeInfo> inferred)
    {
        InferPairwiseBindings(patterns: patternRoutine.ParameterTypes,
            concretes: concreteRoutine.ParameterTypes, inferred: inferred);
        if (patternRoutine.ReturnType != null && concreteRoutine.ReturnType != null)
        {
            InferGenericBindings(pattern: patternRoutine.ReturnType,
                concrete: concreteRoutine.ReturnType, inferred: inferred);
        }
    }

    /// <summary>Infers generic bindings pairwise across two positionally-aligned type lists.</summary>
    private static void InferPairwiseBindings(List<TypeInfo> patterns,
        List<TypeInfo> concretes, Dictionary<string, TypeInfo> inferred)
    {
        for (int i = 0; i < patterns.Count && i < concretes.Count; i++)
        {
            InferGenericBindings(pattern: patterns[i], concrete: concretes[i], inferred: inferred);
        }
    }

    private static void InferGenericLlvmBindings(TypeInfo pattern, string concreteLlvmType,
        Dictionary<string, string> inferredLlvmTypes)
    {
        if (pattern is GenericParameterTypeInfo genericParam)
        {
            inferredLlvmTypes.TryAdd(key: genericParam.Name, value: concreteLlvmType);
        }
    }

    private static bool LooksLikeLlvmType(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value == "void" ||
               value == "ptr" ||
               value == "half" ||
               value == "float" ||
               value == "double" ||
               value == "fp128" ||
               value.StartsWith('i') &&
               value.Length > 1 &&
               char.IsDigit(value[1]) ||
               value.StartsWith('%') ||
               value.StartsWith('{') ||
               value.StartsWith('[');
    }

    /// <summary>
    /// Emits LLVM IR from a template mold string with <c>{hole}</c> substitution.
    /// Supports multi-line templates (for overflow intrinsics, alloca/GEP patterns, etc.).
    /// </summary>
    /// LLVM rejects bitcast between integer and pointer types. The reinterpret_bits intrinsic
    /// template emits `bitcast {From} {value} to {To}`, which is invalid when one side is `ptr`
    /// and the other is `iN`. Rewrite those cases to use `inttoptr` / `ptrtoint`.
    /// <summary>
    /// Detects `%result = bitcast %Record.Foo %val to ptr` (struct -> ptr) which LLVM
    /// rejects, and returns the parts needed to rewrite as `alloca + store` so the result
    /// name aliases a fresh stack slot of the same kind (ptr). Returns null for any line
    /// that isn't a struct -> ptr bitcast.
    /// </summary>
    /// <summary>
    /// Splits a bitcast operand string <c>"&lt;Type&gt; &lt;Value&gt;"</c> into its type and value.
    /// Most types are space-free, but inline-array aggregates (<c>[N x T]</c>, possibly nested) contain
    /// spaces — split after the balanced closing bracket in that case; otherwise split on the first
    /// space. Returns null when the operand is malformed.
    /// </summary>
    private static (string FromType, string Val)? SplitBitcastOperand(string operand)
    {
        // Bracketed types carry internal spaces (`[4 x float]`, `<4 x float>`), so a naive first-space split
        // truncates the type (`<4`). Depth-match the matching close bracket and take the value after it.
        if (operand.StartsWith(value: '[') || operand.StartsWith(value: '<'))
        {
            char open = operand[index: 0];
            char closeCh = open == '[' ? ']' : '>';
            int depth = 0;
            int close = -1;
            for (int i = 0; i < operand.Length; i++)
            {
                if (operand[index: i] == open) depth++;
                else if (operand[index: i] == closeCh && --depth == 0)
                {
                    close = i;
                    break;
                }
            }
            if (close < 0 || close + 1 >= operand.Length) return null;
            return (operand.Substring(startIndex: 0, length: close + 1),
                operand.Substring(startIndex: close + 1).Trim());
        }

        int sep = operand.IndexOf(value: ' ');
        if (sep < 0) return null;
        return (operand.Substring(startIndex: 0, length: sep),
            operand.Substring(startIndex: sep + 1));
    }

    private static (string StructType, string Value, string ResultName)?
        TryDetectStructToPtrBitcast(string line)
    {
        int eqIdx = line.IndexOf(value: " = bitcast ", comparisonType: StringComparison.Ordinal);
        if (eqIdx < 0) return null;
        string resultName = line.Substring(startIndex: 0, length: eqIdx);
        int valueStart = eqIdx + " = bitcast ".Length;
        int toIdx = line.IndexOf(value: " to ", startIndex: valueStart,
            comparisonType: StringComparison.Ordinal);
        if (toIdx < 0) return null;
        string operand = line.Substring(startIndex: valueStart, length: toIdx - valueStart);
        if (SplitBitcastOperand(operand: operand) is not { } split)
        {
            return null;
        }
        (string fromType, string val) = split;
        string toType = line.Substring(startIndex: toIdx + 4).Trim();
        if (toType != "ptr") return null;
        // Struct types in our IR are named %"Record.X" or %"Entity.X" or anonymous %Record.X.
        // Also handle non-pointer scalar value types that can't bitcast to ptr — e.g.
        // `fp128` (used by F128's @llvm("fp128") layout). Integer→ptr is already rewritten
        // to inttoptr upstream by FixIntPtrBitcast; ptr→ptr is a no-op and need not match.
        bool isStruct = fromType.StartsWith(value: '%');
        bool isFloat = fromType is "fp128" or "double" or "float" or "half" or "bfloat" or "x86_fp80" or "ppc_fp128";
        // Inline-array aggregate backends (`[N x T]`, e.g. Array[T,N] / BitArray[N]) also cannot
        // bitcast to ptr. Their universal `get_address`/`hijack` bodies are dead code (the real call
        // is intercepted at the call site), but the materialized definition must still compile —
        // spill the aggregate to a fresh alloca and use its pointer, same as the struct case.
        bool isArray = fromType.StartsWith(value: '[');
        // SIMD vector backends (`<N x T>`, e.g. Simd.Vector[F32, 4]'s @llvm("<{N} x {T}>") layout) likewise
        // cannot bitcast to ptr — same dead-but-must-compile universal get_address/hijack body. Spill too.
        bool isVector = fromType.StartsWith(value: '<');
        if (!isStruct && !isFloat && !isArray && !isVector) return null;
        return (fromType, val, resultName);
    }

    private static string FixIntPtrBitcast(string line)
    {
        const string marker = "= bitcast ";
        int idx = line.IndexOf(value: marker, comparisonType: StringComparison.Ordinal);
        if (idx < 0) return line;
        int valueStart = idx + marker.Length;
        int toIdx = line.IndexOf(value: " to ", startIndex: valueStart,
            comparisonType: StringComparison.Ordinal);
        if (toIdx < 0) return line;
        string fromType = line.Substring(startIndex: valueStart, length: toIdx - valueStart)
            .Split(separator: ' ')[0];
        string toType = line.Substring(startIndex: toIdx + 4).Trim();
        bool fromIsInt = fromType.Length > 1 && fromType[0] == 'i' && char.IsDigit(c: fromType[1]);
        bool toIsInt = toType.Length > 1 && toType[0] == 'i' && char.IsDigit(c: toType[1]);
        bool fromIsPtr = fromType == "ptr";
        bool toIsPtr = toType == "ptr";
        if (fromIsInt && toIsPtr)
            return string.Concat(line.AsSpan(start: 0, length: idx + 2), "inttoptr ",
                line.AsSpan(start: valueStart));
        if (fromIsPtr && toIsInt)
            return string.Concat(line.AsSpan(start: 0, length: idx + 2), "ptrtoint ",
                line.AsSpan(start: valueStart));
        return line;
    }

    private string EmitFromTemplate(StringBuilder sb, string mold, RoutineInfo memberRoutine,
        List<string> llvmTypeArgs, List<string> args)
    {
        string[] lines = mold.Split(separator: '\n', options: StringSplitOptions.RemoveEmptyEntries);
        string? lastResult = null;
        string? prevResult = null;
        string? firstResult = null;

        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim();
            if (line.Length == 0) continue;

            string currentResult = NextTemp();
            bool hasResult = line.Contains(value: "{result}");

            string substituted = SubstituteTemplateLine(line: line, memberRoutine: memberRoutine,
                llvmTypeArgs: llvmTypeArgs, args: args, currentResult: currentResult,
                prevResult: prevResult, firstResult: firstResult);

            // `bitcast %Record.Foo %val to ptr` is illegal in LLVM (struct -> ptr bitcasts
            // are forbidden). Routines like the universal `T.get_address()` use
            // `reinterpret_bits[T, Hijacked[T]](me)` and would emit this for record T.
            // Real callers of `record.get_address()` are intercepted at the call site
            // (see EmitMemberRoutineCall) so the body's address is never observed in
            // practice — but the body still has to compile. Materialize the struct into a
            // fresh alloca and use that pointer instead: same kind (ptr), valid IR, and
            // the dead-code body has a well-defined (callee-local, immediately-dropped)
            // address. Emits three IR lines instead of one for this specific case.
            (string structType, string val, string resultName)? rewritten =
                TryDetectStructToPtrBitcast(line: substituted);
            if (rewritten.HasValue)
            {
                var (structType2, val2, resultName2) = rewritten.Value;
                EmitLine(sb: sb, line: $"  {resultName2} = alloca {structType2}");
                EmitLine(sb: sb, line: $"  store {structType2} {val2}, ptr {resultName2}");
                if (hasResult)
                {
                    firstResult ??= currentResult;
                    prevResult = currentResult;
                    lastResult = currentResult;
                }
                continue;
            }

            EmitLine(sb: sb, line: $"  {substituted}");

            if (hasResult)
            {
                firstResult ??= currentResult;
                prevResult = currentResult;
                lastResult = currentResult;
            }
        }

        // Overflow intrinsics return anonymous struct types like { i128, i1 }.
        // If the memberRoutine's return type is a TupleTypeInfo, coerce via extractvalue/insertvalue
        // so the caller receives the named LLVM type (%"Record.Tuple[...]").
        if (lastResult != null && memberRoutine.ReturnType is TupleTypeInfo tupleReturn)
        {
            return CoerceAnonStructToNamedTuple(sb: sb, tupleReturn: tupleReturn,
                lastResult: lastResult);
        }

        return lastResult ?? (args.Count > 0 ? args[index: 0] : "undef");
    }

    /// <summary>
    /// Performs all <c>{hole}</c> substitutions on one template line: the <c>{result}</c>/<c>{prev}</c>/
    /// <c>{first}</c> temporaries, named generic-parameter types (and their <c>{sizeof T}</c> byte
    /// widths), positional <c>{paramName}</c> argument values, arithmetic const-generic holes, and the
    /// int↔ptr bitcast fixup.
    /// </summary>
    private string SubstituteTemplateLine(string line, RoutineInfo memberRoutine,
        List<string> llvmTypeArgs, List<string> args, string currentResult,
        string? prevResult, string? firstResult)
    {
        string substituted = line;
        substituted = substituted.Replace(oldValue: "{result}", newValue: currentResult);

        if (prevResult != null)
            substituted = substituted.Replace(oldValue: "{prev}", newValue: prevResult);

        if (firstResult != null)
            substituted = substituted.Replace(oldValue: "{first}", newValue: firstResult);

        // {T}, {From}, {To}, etc. — named generic parameters -> LLVM types
        List<string>? genericParameters =
            memberRoutine.GenericParameters ?? memberRoutine.GenericDefinition?.GenericParameters;
        if (genericParameters != null)
        {
            for (int i = 0; i < genericParameters.Count && i < llvmTypeArgs.Count; i++)
            {
                string paramName = genericParameters[index: i];
                substituted = substituted.Replace(oldValue: $"{{{paramName}}}",
                    newValue: llvmTypeArgs[index: i]);

                string sizeofPattern = $"{{sizeof {paramName}}}";
                if (substituted.Contains(value: sizeofPattern))
                {
                    substituted = substituted.Replace(oldValue: sizeofPattern,
                        newValue: (GetTypeBitWidth(llvmType: llvmTypeArgs[index: i]) / 8)
                                  .ToString());
                }
            }
        }

        // {paramName} -> emitted arg value (positional by parameter list order)
        for (int i = 0; i < memberRoutine.Parameters.Count && i < args.Count; i++)
        {
            string paramName = memberRoutine.Parameters[index: i].Name;
            substituted = substituted.Replace(oldValue: $"{{{paramName}}}",
                newValue: args[index: i]);
        }

        // Arithmetic holes over const generic params: {(N+7)//8}, {N*2}, etc.
        // BackendType templates (resolved in RecordTypeInfo.CreateInstance) handle these
        // already; @llvm_ir templates need the same support so e.g. BitArray[N]'s
        // byte_at_bits intrinsic emits `[1 x i8]` for N=8 instead of `[{(N+7)//8} x i8]`.
        substituted = ResolveArithmeticHoles(template: substituted, memberRoutine: memberRoutine,
            llvmTypeArgs: llvmTypeArgs);

        return FixIntPtrBitcast(line: substituted);
    }

    /// <summary>
    /// Coerces an anonymous-struct intrinsic result (e.g. overflow intrinsics returning
    /// <c>{ i128, i1 }</c>) into the named tuple LLVM type via per-element extractvalue/insertvalue,
    /// Bool-zext'ing each element to its i8 storage form. Returns the built named-tuple SSA value.
    /// </summary>
    private string CoerceAnonStructToNamedTuple(StringBuilder sb, TupleTypeInfo tupleReturn,
        string lastResult)
    {
        string namedType = GetLlvmType(type: tupleReturn);
        string anonType =
            $"{{ {string.Join(separator: ", ", values: tupleReturn.ElementTypes.Select(selector: GetLlvmType))} }}";
        string tupleVal = "undef";
        for (int i = 0; i < tupleReturn.ElementTypes.Count; i++)
        {
            string elem = NextTemp();
            EmitLine(sb: sb, line: $"  {elem} = extractvalue {anonType} {lastResult}, {i}");
            // The named tuple stores a Bool element as i8 — zext the i1 from the anon result.
            TypeInfo elemType = tupleReturn.ElementTypes[index: i];
            elem = CoerceBoolToStorage(sb: sb, value: elem, fieldType: elemType);
            string ins = NextTemp();
            EmitLine(sb: sb,
                line: $"  {ins} = insertvalue {namedType} {tupleVal}, {GetFieldStorageLlvmType(type: elemType)} {elem}, {i}");
            tupleVal = ins;
        }
        return tupleVal;
    }

    /// <summary>
    /// Resolves a <see cref="TypeExpression"/> to its LLVM type string,
    /// applying active type substitutions and registry lookups.
    /// </summary>
    private string ResolveTypeExpressionToLlvm(TypeExpression typeExpr)
    {
        if (typeExpr.ResolvedType is { } resolvedType and not ErrorTypeInfo)
        {
            return GetLlvmType(type: ApplyTypeSubstitutions(type: resolvedType));
        }

        var type = _registry.LookupType(name: typeExpr.Name);
        if (type != null)
        {
            return type.IsGenericDefinition && typeExpr.GenericArguments is { Count: > 0 }
                ? ResolveGenericDefinitionLlvm(typeExpr: typeExpr, genericDef: type)
                : GetLlvmType(type: type);
        }

        type = LookupTypeInCurrentModule(name: typeExpr.Name);
        return type != null ? GetLlvmType(type: type) : typeExpr.Name;
    }

    /// <summary>
    /// Resolves a generic-definition type expression (e.g. <c>List[S64]</c>) to its LLVM type —
    /// preferring a registered full-name instance, else resolving the arguments and instantiating.
    /// Falls back to the bare generic-definition LLVM type when the arity doesn't match.
    /// </summary>
    private string ResolveGenericDefinitionLlvm(TypeExpression typeExpr, TypeInfo genericDef)
    {
        string fullName =
            $"{typeExpr.Name}[{string.Join(separator: ", ", values: typeExpr.GenericArguments!.Select(selector: g => g.Name))}]";
        var fullType = _registry.LookupType(name: fullName);
        if (fullType != null)
        {
            return GetLlvmType(type: fullType);
        }

        var resolvedArgs = new List<TypeInfo>();
        foreach (TypeExpression ga in typeExpr.GenericArguments!)
        {
            var r = ResolveTypeArgument(ta: ga);
            if (r != null)
            {
                resolvedArgs.Add(r);
            }
        }
        return resolvedArgs.Count == genericDef.GenericParameters!.Count
            ? GetLlvmType(type: _registry.GetOrCreateResolution(
                genericDef: genericDef, typeArguments: resolvedArgs))
            : GetLlvmType(type: genericDef);
    }

    /// <summary>
    /// Fallback handler for <see cref="GenericMemberRoutineCallExpression"/> nodes that reached codegen
    /// without being lowered. Handles LLVM intrinsic free-function GMCEs by looking up the routine
    /// in the registry. Throws for any non-intrinsic GMCE (contract violation).
    /// </summary>
    private string EmitGmceFallback(StringBuilder sb, GenericMemberRoutineCallExpression gmc)
    {
        RoutineInfo? routine = gmc.ResolvedRoutine;

        // Signature-only registry lookup for unresolved free-function calls (Object.Name == memberRoutineName).
        if (routine == null && gmc.Object is IdentifierExpression freeId &&
            freeId.Name == gmc.MemberRoutineName)
        {
            routine = _registry.LookupRoutineOverload(baseName: gmc.MemberRoutineName,
                argTypes: gmc.Arguments
                    .Select(selector: a => GetExpressionType(
                        expr: a is NamedArgumentExpression na ? na.Value : a))
                    .OfType<TypeInfo>()
                    .ToList());
        }

        if (routine?.LlvmIrTemplate != null)
            return EmitLlvmIntrinsicCall(sb: sb, routine: routine, receiver: null,
                arguments: gmc.Arguments, typeArguments: gmc.TypeArguments,
                resolvedReturnType: gmc.ResolvedType);

        string objectDesc = gmc.Object is IdentifierExpression id2 ? id2.Name : gmc.Object.GetType().Name;
        string typeArgDesc = (gmc.TypeArguments?.Select(
            t => $"{t.Name}(resolved={t.ResolvedType?.FullName ?? "null"})").ToList() ?? []).Repr();
        string routineDesc = gmc.ResolvedRoutine is { } rr
            ? $"key={rr.RegistryKey} isGenDef={rr.IsGenericDefinition} hasGenDef={rr.GenericDefinition != null} typeArgs={(rr.TypeArguments?.Select(t => t.FullName).ToList() ?? []).Repr()}"
            : "ResolvedRoutine=NULL";
        throw new InvalidOperationException(
            $"GenericMemberRoutineCallExpression reached codegen — GenericCallLoweringPass must lower all GMCEs to CallExpression before codegen. " +
            $"GMCE: {objectDesc}.{gmc.MemberRoutineName}[{typeArgDesc}] ({routineDesc}), " +
            $"in routine: {_currentEmittingRoutine?.Name ?? "<unknown>"} (owner: {_currentEmittingRoutine?.OwnerType?.Name ?? "none"})");
    }
}
