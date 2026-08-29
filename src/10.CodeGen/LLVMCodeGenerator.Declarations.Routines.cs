using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Compiler.Targeting;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;
using Verification.Enums;

namespace Compiler.CodeGen;

public partial class LlvmCodeGenerator
{
    private void GenerateRoutineDeclaration(RoutineInfo routine, string? nameOverride = null)
    {
        // Record the reference: if we are emitting a routine body, this routine is a real callee and
        // must itself be emitted. Gated on _emittingRoutineBody so the broad declaration pre-pass
        // (which declares much of the registry up front) does not mark everything referenced.
        if (_emittingRoutineBody)
        {
            _referencedKeys.Add(item: routine.RegistryKey);
            // Also record the realm-stripped form: mangling is realm-free, so a body keyed under a
            // different world-line (SF:: vs ambient) is the SAME emitted symbol and must pass the gate.
            _referencedKeys.Add(item: StripRealmPrefix(routine.RegistryKey));
        }

        string funcName = nameOverride ?? MangleRoutineName(routine: routine);

        // Skip if already generated
        if (_generatedRoutines.Contains(item: funcName))
        {
            return;
        }

        // Skip @innate routines — compile-time stubs, never have bodies, must not reach codegen
        if (routine.Annotations.Contains(value: "innate"))
            return;

        // Skip declarations on generic-definition owner types (e.g. T, List[T]) —
        // these produce invalid LLVM IR; only monomorphized concrete instances should be declared
        if (routine.OwnerType?.IsGenericDefinition == true)
            return;

        // Skip declarations that reference unresolved generic parameter types —
        // these produce invalid LLVM IR (e.g., Maybe[BTreeDictNode[K, V]] instead of concrete types)
        if (routine.Parameters.Any(predicate: p =>
                ContainsGenericParameter(type: p.Type)) ||
            routine.ReturnType != null && ContainsGenericParameter(type: routine.ReturnType) ||
            routine.OwnerType != null && ContainsGenericParameter(type: routine.OwnerType))
        {
            return;
        }

        _generatedRoutines.Add(item: funcName);

        // Build parameter list
        var paramTypes = new List<string>();

        // For memberRoutines, add implicit 'me' parameter first
        // Skip 'me' for create routines (static factories) and common (type-level) routines
        bool isCreator = IsCreatorRoutine(routine: routine);
        if (routine.OwnerType != null && !isCreator && !routine.IsCommon)
        {
            // setitem! on records: me is passed by pointer so mutations propagate to caller
            paramTypes.Add(item: GetImplicitMeParameterDeclaration(routine: routine,
                includeName: false));
        }

        // Add explicit parameters
        // For external("C") functions, F16 (half) must be passed as i16 (C ABI uses integer register)
        bool isCExtern = routine.CallingConvention == "C";
        paramTypes.AddRange(collection: routine.Parameters.Select(selector: param =>
        {
            // By-ref struct-record thread arg: the worker receives a pointer to the spawner's cell.
            if (IsByRefThreadArg(routine: routine, param: param)) return "ptr";
            // ABI-Indirect struct value arg: passed as a hidden byval pointer-to-copy.
            if (ParameterPassedByval(routine: routine, paramType: param.Type))
                return $"ptr byval({GetLlvmType(type: param.Type)})";
            // ABI-Coerce small struct value arg: passed reinterpreted as an integer register form.
            if (ParameterCoerceType(routine: routine, paramType: param.Type) is { } coerceArg)
                return coerceArg;
            string t = GetParameterLlvmType(type: param.Type);
            if (isCExtern && t == "half") return "i16";
            string attrs = GetExplicitParameterAttributes(type: param.Type);
            return string.IsNullOrEmpty(attrs) ? t : $"{t} {attrs}";
        }));

        // Ensure record type definitions exist for parameter and return types
        foreach (ParameterInfo param in routine.Parameters)
        {
            if (param.Type is RecordTypeInfo { HasDirectBackendType: false, IsGenericDefinition: false } paramRecord)
            {
                GenerateRecordType(record: paramRecord);
            }
        }

        if (routine.ReturnType is RecordTypeInfo { HasDirectBackendType: false, IsGenericDefinition: false } returnRecord)
        {
            GenerateRecordType(record: returnRecord);
        }

        // Get return type
        string returnType = routine.ReturnType != null
            ? GetLlvmType(type: routine.ReturnType)
            : "void";
        if (routine.FailableVariant == FailableVariant.Lookup)
        {
            // Lookup[None] degenerates to Result[None]: a None value payload makes the
            // "found vs not-found" distinction meaningless, so use the Result carrier instead.
            returnType = routine.ReturnType?.IsNone == true
                ? GetResultCarrierLlvmType(valueType: routine.ReturnType!)
                : GetLookupCarrierLlvmType(valueType: routine.ReturnType!);
        }
        else if (routine.FailableVariant == FailableVariant.Check)
        {
            returnType = GetResultCarrierLlvmType(valueType: routine.ReturnType!);
        }
        else if (routine.FailableVariant == FailableVariant.TryBool)
        {
            returnType = "i1";
        }

        if (isCExtern && returnType == "half")
        {
            returnType = "i16";
        }

        // Struct returns classified Indirect by the target ABI go through a hidden sret pointer.
        // For external("C") this matches the platform C ABI (Win-x64 MSVC: structs > 8 bytes);
        // for RF routines it is the ABI boundary-coercion return form. The declaration, definition,
        // every return, and every call site must agree — see ReturnsViaSret / _currentReturnViaSret.
        bool needsSret = isCExtern
            ? NeedsCExternSret(routine: routine)
            : ReturnsViaSret(routine: routine);
        // Phase 2: a small struct return is coerced to an integer register form — the declared
        // return type becomes that, matching the define/return/call sites. (Not for C externs.)
        string? declCoerceReturn = isCExtern || needsSret ? null : ReturnCoerceType(routine: routine);
        if (needsSret)
        {
            // Change declaration: void @func(ptr sret(%RecordType), original_params...)
            paramTypes.Insert(index: 0, item: $"ptr sret({returnType})");
            string parameters = string.Join(separator: ", ", values: paramTypes);
            _rfRoutineDeclarations[key: funcName] = $"declare void @{funcName}({parameters})";
        }
        else if (declCoerceReturn != null)
        {
            string parameters = string.Join(separator: ", ", values: paramTypes);
            _rfRoutineDeclarations[key: funcName] =
                $"declare {declCoerceReturn} @{funcName}({parameters})";
        }
        else
        {
            string parameters = string.Join(separator: ", ", values: paramTypes);
            string returnPrefix = isCreator && returnType == "ptr" ? "noalias " : "";
            _rfRoutineDeclarations[key: funcName] =
                $"declare {returnPrefix}{returnType} @{funcName}({parameters})";
        }

        // Over-prune tripwire: this declare was emitted because an emitted body references the
        // routine, and it is a real RF routine (not a bodyless C extern), so it MUST also be
        // defined. Record it so the post-fixpoint check in GenerateRoutineDefinitions can catch a
        // reachability over-prune (a called routine whose body got dropped) as a located codegen
        // error rather than a downstream linker "undefined symbol". Gated on _emittingRoutineBody
        // so the broad declaration pre-pass — which declares much of the registry but references
        // nothing — does not enroll dead routines.
        if (_emittingRoutineBody && !isCExtern)
        {
            _expectedBodyNames.Add(item: funcName);
        }
    }

    /// <summary>
    /// Returns the LLVM named-struct type for a lifted lambda's BOUND payload (v0.4.1) —
    /// <c>%"Closure.&lt;liftedName&gt;" = type { &lt;capture types&gt; }</c> — declaring it on first use.
    /// The payload holds ONLY the captured (pre-bound) values in <see cref="RoutineInfo.ClosureCaptures"/>
    /// order (capture 0 at field 0). The function pointer is NOT in here — it lives in element 0 of the
    /// fat Routine value <c>{ ptr fn, ptr bound }</c>; this struct is what <c>bound</c> points to, and
    /// it is passed to the body as a trailing <c>ptr %__bound</c> (= C userdata). See [[cabi-callback-ffi]].
    /// </summary>
    /// <param name="lambda">The lifted lambda's RoutineInfo; its Name and ClosureCaptures determine the struct name and fields.</param>
    private string ClosureStructName(RoutineInfo lambda)
    {
        string name = $"%\"Closure.{lambda.Name}\"";
        if (_typeDeclarationsClosure.ContainsKey(key: name))
        {
            return name;
        }

        var fields = new List<string>();
        if (lambda.ClosureCaptures != null)
        {
            foreach ((string _, TypeInfo capType) in lambda.ClosureCaptures)
                fields.Add(item: GetLlvmType(type: capType));
        }
        _typeDeclarationsClosure[key: name] =
            $"{name} = type {{ {string.Join(separator: ", ", values: fields)} }}\n";
        return name;
    }

    private void GenerateRoutineDefinition(RoutineDeclaration routine,
        RoutineInfo? preResolvedInfo = null, string? nameOverride = null,
        string? moduleContext = null)
    {
        // The binding attached at registration is authoritative — it is the exact RoutineInfo this
        // declaration was registered as, so it needs no name re-parsing or module-blind owner lookup.
        RoutineInfo? routineInfo = preResolvedInfo ?? routine.ResolvedInfo
            ?? ResolveRoutineInfoForDefinition(routine: routine, moduleContext: moduleContext);

        if (ShouldSkipRoutineDefinition(routineInfo: routineInfo))
        {
            return;
        }

        RoutineInfo info = routineInfo!;
        string funcName = nameOverride ?? MangleRoutineName(routine: info);

        // Skip if already generated (prevents duplicates between user program and stdlib)
        if (!_generatedRoutineDefs.Add(item: funcName))
        {
            return;
        }

        // Also mark as generated in declarations set to prevent declare/define conflicts
        _generatedRoutines.Add(item: funcName);

        bool isCreator = IsCreatorRoutine(routine: info);
        List<string> paramList = BuildDefinitionParameterList(info: info);
        string returnType = ComputeDefinitionReturnType(info: info);

        // Struct returns classified Indirect by the ABI are returned through a hidden sret pointer:
        // prepend `ptr sret(%T) %sret` and make the header return void; every `return` in the body
        // then stores through %sret (see EmitReturn). _currentReturnViaSret is read during body
        // emission, so set it before GenerateRoutineBody and restore after.
        bool prevReturnViaSret = _currentReturnViaSret;
        string? prevReturnCoerce = _currentReturnCoerceType;
        _currentReturnViaSret = ReturnsViaSret(routine: info);
        // Phase 2: a small struct return is coerced to an integer register form; the header returns
        // that type and every `return` reinterprets the struct into it (see EmitReturn).
        _currentReturnCoerceType = _currentReturnViaSret ? null : ReturnCoerceType(routine: info);
        if (_currentReturnViaSret)
        {
            paramList.Insert(index: 0, item: $"ptr sret({returnType}) %sret");
        }

        // Start function — save position so we can rollback on error
        string parameters = string.Join(separator: ", ", values: paramList);
        int savedLength = _functionDefinitions.Length;
        int savedTempCounter = _tempCounter;

        string defineHeader = BuildDefineHeader(info: info, funcName: funcName,
            parameters: parameters, returnType: returnType, isCreator: isCreator);
        _generatedRoutineDefHeaders[key: funcName] = defineHeader;
        RecordDebugSubprogram(funcName: funcName, location: info.Location);
        _currentDbgLoc = null; // reset the Layer-2 location cursor at each routine boundary
        EmitLine(sb: _functionDefinitions, line: defineHeader);
        EmitLine(sb: _functionDefinitions, line: "entry:");
        var bodyBuilder = new StringBuilder();

        try
        {
            EmitDefinitionBody(routine: routine, info: info, bodyBuilder: bodyBuilder);
        }
        catch
        {
            // Rollback partial IR so the output stays well-formed, then re-throw so the
            // caller can decide whether to skip or abort compilation.
            _functionDefinitions.Length = savedLength;
            _tempCounter = savedTempCounter;
            _generatedRoutineDefs.Remove(item: funcName);
            _generatedRoutineDefHeaders.Remove(key: funcName);
            _generatedRoutines.Remove(item: funcName);
            throw;
        }

        // End function
        EmitLine(sb: _functionDefinitions, line: "}");
        EmitLine(sb: _functionDefinitions, line: "");
        _currentReturnViaSret = prevReturnViaSret;
        _currentReturnCoerceType = prevReturnCoerce;
    }

    /// <summary>
    /// Resolves the <see cref="RoutineInfo"/> for a routine definition from the registry when the
    /// declaration carries no authoritative binding. Prefers module-scoped owner resolution so two
    /// modules that each declare a same-named type/routine each bind to their own symbol.
    /// </summary>
    private RoutineInfo? ResolveRoutineInfoForDefinition(RoutineDeclaration routine,
        string? moduleContext)
    {
        // The owner-qualified composite key (bare member for a free routine). Used AS-IS for the
        // whole-key registry lookups; the scoped/unqualified helpers read the routine's STRUCTURED
        // owner/member fields instead of re-splitting this string.
        string baseName = routine.QualifiedName;
        RoutineInfo? routineInfo = LookupScopedMemberRoutine(routine: routine,
            moduleContext: moduleContext);

        routineInfo ??= _registry.LookupRoutine(fullName: baseName);
        // Module-level routine (no owner): prefer the module-qualified key so that two modules'
        // same-named routines each bind to their OWN RoutineInfo.
        if (routineInfo == null && !string.IsNullOrEmpty(value: moduleContext) &&
            routine.OwnerName is null)
        {
            routineInfo = _registry.LookupRoutine(fullName: $"{moduleContext}.{baseName}");
        }
        routineInfo ??= LookupUnqualifiedRoutine(routine: routine, moduleContext: moduleContext);

        return ResolveDefinitionOverload(routine: routine, moduleContext: moduleContext,
            routineInfo: routineInfo);
    }

    /// <summary>
    /// Owner-scoped member lookup pinned to <paramref name="moduleContext"/>. A bare
    /// <c>LookupRoutine("Box.destroy")</c> returns a first-wins entry, so a 0-param memberRoutine on a
    /// same-named type in another module would otherwise be emitted under the wrong symbol.
    /// </summary>
    private RoutineInfo? LookupScopedMemberRoutine(RoutineDeclaration routine, string? moduleContext)
    {
        // OwnerName is the canonical BARE owner (generic args live in RenderedReceiver); MemberRoutineName
        // is the bare member. Reading them structurally avoids re-splitting QualifiedName on '.'.
        if (string.IsNullOrEmpty(value: moduleContext) || routine.OwnerName is not { } ownerSeg)
        {
            return null;
        }

        TypeInfo? scopedOwner = _registry.LookupType(name: $"{moduleContext}.{ownerSeg}");
        return scopedOwner == null
            ? null
            : _registry.LookupMemberRoutine(type: scopedOwner, memberRoutineName: routine.MemberRoutineName!);
    }

    /// <summary>
    /// Fallback lookup when the earlier module-scoped and full-name lookups miss. For a member name
    /// (with a dot) resolves owner-scoped first; otherwise uses the short-name registry fallback.
    /// </summary>
    private RoutineInfo? LookupUnqualifiedRoutine(RoutineDeclaration routine, string? moduleContext)
    {
        if (routine.OwnerName is not { } ownerPart)
        {
            // Free routine (or type-body member) — bare short-name fallback (e.g. "show" -> "IO.show").
            return _registry.LookupRoutineByName(name: routine.Name);
        }

        // Member declaration (e.g. "UnpackedFloat[M, L, W].cbrt"). Resolve scoped to the owner
        // type FIRST so a bare short-name lookup does not bind to a same-named routine of another
        // owner. OwnerName/MemberRoutineName are the parser's structured pieces — no '.'-splitting.
        string shortName = routine.MemberRoutineName!;
        TypeInfo? ownerType = (!string.IsNullOrEmpty(value: moduleContext)
            ? _registry.LookupType(name: $"{moduleContext}.{ownerPart}")
            : null) ?? _registry.LookupType(name: ownerPart);
        RoutineInfo? routineInfo = ownerType != null
            ? _registry.LookupMemberRoutine(type: ownerType, memberRoutineName: shortName)
            : null;
        return routineInfo ?? _registry.LookupRoutine(fullName: shortName) ??
            _registry.LookupRoutineByName(name: shortName);
    }

    /// <summary>
    /// For an overloaded routine, resolves the specific overload matching this AST's parameter
    /// types (owner-scoped first for member declarations), returning the more specific match.
    /// </summary>
    private RoutineInfo? ResolveDefinitionOverload(RoutineDeclaration routine,
        string? moduleContext, RoutineInfo? routineInfo)
    {
        if (routineInfo == null || routine.Parameters.Count == 0)
        {
            return routineInfo;
        }

        List<TypeInfo> astParamTypes = ResolveAstParameterTypes(routine: routine);
        if (astParamTypes.Count != routine.Parameters.Count)
        {
            return routineInfo;
        }

        RoutineInfo? overload = ResolveScopedMemberRoutineOverload(routine: routine,
            moduleContext: moduleContext, astParamTypes: astParamTypes);
        overload ??= _registry.LookupRoutineOverload(baseName: routineInfo.BaseName,
            argTypes: astParamTypes);
        return overload ?? routineInfo;
    }

    /// <summary>
    /// Resolves each AST parameter's declared type to a registered <see cref="TypeInfo"/>.
    /// </summary>
    private List<TypeInfo> ResolveAstParameterTypes(RoutineDeclaration routine)
    {
        var astParamTypes = new List<TypeInfo>();
        foreach (Parameter param in routine.Parameters)
        {
            if (param.Type == null)
            {
                continue;
            }

            string typeName = param.Type.Name;
            if (param.Type.GenericArguments is { Count: > 0 } genArgs)
            {
                typeName =
                    $"{typeName}[{string.Join(separator: ", ", values: genArgs.Select(selector: a => a.Name))}]";
            }

            TypeInfo? t = _registry.LookupType(name: typeName);
            if (t != null)
            {
                astParamTypes.Add(item: t);
            }
        }
        return astParamTypes;
    }

    /// <summary>
    /// Owner-scoped overload disambiguation for a member declaration. The base-name overload path
    /// misses here because member base names always contain a '.', which disables the Core-prefix
    /// fallback — so collect the owner type's candidates and match positionally by arg type.
    /// </summary>
    private RoutineInfo? ResolveScopedMemberRoutineOverload(RoutineDeclaration routine,
        string? moduleContext, List<TypeInfo> astParamTypes)
    {
        if (routine.OwnerName is not { } ownerPart || routine.MemberRoutineName is not { } shortName)
        {
            return null;
        }

        TypeInfo? ownerType = (!string.IsNullOrEmpty(value: moduleContext)
            ? _registry.LookupType(name: $"{moduleContext}.{ownerPart}")
            : null) ?? _registry.LookupType(name: ownerPart);
        return ownerType == null
            ? null
            : _registry.LookupMemberRoutineOverload(type: ownerType, memberRoutineName: shortName,
                argTypes: astParamTypes);
    }

    /// <summary>
    /// Gate deciding whether a routine definition must be skipped: unresolved / generic-definition /
    /// generic-param-owner / protocol-owned / unresolved-generic-signature / error-typed routines,
    /// and routines pruned out by the reachability set.
    /// </summary>
    private bool ShouldSkipRoutineDefinition(RoutineInfo? routineInfo)
    {
        if (routineInfo == null || routineInfo.IsGenericDefinition ||
            routineInfo.OwnerType is GenericParameterTypeInfo)
        {
            return true; // generic definitions, unresolved routines, generic-param-owner routines
        }

        // A protocol-extension routine's own template body is never emitted directly —
        // ProtocolDefaultImplLoweringPass clones a concrete copy per implementer.
        if (routineInfo.OwnerType is ProtocolTypeInfo)
        {
            return true;
        }

        if (routineInfo.Parameters.Any(predicate: p => ContainsGenericParameter(type: p.Type)) ||
            routineInfo.ReturnType != null && ContainsGenericParameter(type: routineInfo.ReturnType) ||
            routineInfo.OwnerType != null && ContainsGenericParameter(type: routineInfo.OwnerType))
        {
            return true;
        }

        if (HasErrorTypes(routine: routineInfo))
        {
            return true;
        }

        // Reachability gate: when LiveRoutineKeys is populated, skip routines not reachable from
        // program entry points. Lifted lambdas are exempt — LambdaLiftingPass runs after
        // reachability, so their keys aren't in the live set even when referenced by address.
        return _liveRoutineKeys.Count > 0
            && !_liveRoutineKeys.Contains(item: routineInfo.RegistryKey)
            && !routineInfo.IsLambda;
    }

    /// <summary>
    /// Builds the LLVM parameter list (with names) for a routine definition: the hidden closure
    /// pointer for lambdas, the implicit <c>me</c> receiver for memberRoutines, and each explicit
    /// parameter in its ABI form (by-ref thread arg / byval / coerce / plain value).
    /// </summary>
    private List<string> BuildDefinitionParameterList(RoutineInfo info)
    {
        var paramList = new List<string>();

        // Closure ABI (v0.4.1): a CAPTURING lifted lambda receives its bound payload as a hidden
        // TRAILING `ptr %__bound` parameter (added AFTER the explicit params below) = C's userdata-last
        // convention. A captureless lambda gets NO extra param — its body is a plain C-ABI `ret(args)`.
        // The trailing param is appended at the end of this method, not here.

        // For memberRoutines, add implicit 'me' parameter first (skip create factories, common routines,
        // and void/None owner types).
        if (info.OwnerType != null && !IsCreatorRoutine(routine: info) && !info.IsCommon)
        {
            string meParam = GetImplicitMeParameterDeclaration(routine: info, includeName: true);
            if (!meParam.StartsWith(value: "void", comparisonType: StringComparison.Ordinal))
            {
                paramList.Add(item: meParam);
            }
        }

        // Add explicit parameters (sanitize names that conflict with the reserved "entry" label).
        paramList.AddRange(collection:
            from param in info.Parameters
            select FormatDefinitionParameter(info: info, param: param));

        // v0.4.1 closure ABI: a CAPTURING lifted lambda takes its bound payload as a TRAILING
        // `ptr %__bound` (= C userdata-last). Captureless lambdas / plain routines get nothing.
        if (info.IsLambda && info.ClosureCaptures is { Count: > 0 })
        {
            paramList.Add(item: "ptr %__bound");
        }
        return paramList;
    }

    /// <summary>
    /// Formats a single explicit parameter into its LLVM declaration form, resolving the ABI
    /// passing mode (by-ref thread arg, ABI-Indirect byval, ABI-Coerce integer, or plain value).
    /// </summary>
    private string FormatDefinitionParameter(RoutineInfo info, ParameterInfo param)
    {
        bool byRefThreadArg = IsByRefThreadArg(routine: info, param: param);
        // ABI-Indirect struct value arg: arrives as `ptr byval(%T)` — a pointer to the callee's
        // private copy, doubling as the struct's lvalue address.
        bool byval = !byRefThreadArg && ParameterPassedByval(routine: info, paramType: param.Type);
        // ABI-Coerce small struct value arg: arrives as an integer register value.
        string? coerce = !byRefThreadArg && !byval
            ? ParameterCoerceType(routine: info, paramType: param.Type)
            : null;
        string paramType = byRefThreadArg ? "ptr"
            : byval ? $"ptr byval({GetLlvmType(type: param.Type)})"
            : coerce ?? GetParameterLlvmType(type: param.Type);
        string paramAttrs = byRefThreadArg || byval || coerce != null
            ? string.Empty
            : GetExplicitParameterAttributes(type: param.Type);
        string emittedName = byRefThreadArg || byval ? $"{param.Name}.addr"
            : param.Name == "entry" ? "entry_" : param.Name;
        return string.IsNullOrEmpty(paramAttrs)
            ? $"{paramType} %{emittedName}"
            : $"{paramType} {paramAttrs} %{emittedName}";
    }

    /// <summary>
    /// Computes the LLVM return type for a routine definition, applying the failable-variant carrier
    /// (Lookup / Check / TryBool) forms.
    /// </summary>
    private string ComputeDefinitionReturnType(RoutineInfo info)
    {
        return info.FailableVariant switch
        {
            FailableVariant.Lookup => GetLookupCarrierLlvmType(valueType: info.ReturnType!),
            FailableVariant.Check => GetResultCarrierLlvmType(valueType: info.ReturnType!),
            FailableVariant.TryBool => "i1",
            _ => info.ReturnType != null ? GetLlvmType(type: info.ReturnType) : "void"
        };
    }

    /// <summary>
    /// Assembles the <c>define …</c> header line: linkage/return prefixes, header return type
    /// (void for sret, coerced type otherwise) and per-routine attributes (inline / nounwind /
    /// no_optimize). Reads <see cref="_currentReturnViaSret"/> / <see cref="_currentReturnCoerceType"/>.
    /// </summary>
    private string BuildDefineHeader(RoutineInfo info, string funcName, string parameters,
        string returnType, bool isCreator)
    {
        string headerReturnType = _currentReturnViaSret ? "void"
            : _currentReturnCoerceType ?? returnType;
        string returnPrefix =
            !_currentReturnViaSret && isCreator && returnType == "ptr" ? "noalias " : "";
        // Compiler-synthesized routines are only ever referenced within this whole-program module,
        // so give them `internal` linkage (GlobalDCE can strip uncalled ones) + `nounwind` (the
        // runtime never unwinds). main / start / user routines and extern `declare`s are untouched.
        bool isCompilerGenerated = info.IsSynthesized || info.IsWiredMemberRoutine;
        string linkagePrefix = isCompilerGenerated ? "internal " : "";
        string funcAttrs = info.Annotations.Contains(value: "inline") ? " alwaysinline" : "";
        if (isCompilerGenerated)
        {
            funcAttrs += " nounwind";
        }
        // `@no_optimize` emits `noinline optnone` — a per-routine optimization barrier for the
        // softfloat gamma cores that LLVM 21's InstCombine miscompiles at -O2+.
        if (info.Annotations.Contains(value: "no_optimize"))
        {
            funcAttrs += " noinline optnone";
        }
        return
            $"define {linkagePrefix}{returnPrefix}{headerReturnType} @{funcName}({parameters}){funcAttrs} {{";
    }

    /// <summary>
    /// Emits the routine body (or its synthesized stub body) into <paramref name="bodyBuilder"/> and
    /// appends the entry allocas + body to the function-definitions buffer.
    /// </summary>
    private void EmitDefinitionBody(RoutineDeclaration routine, RoutineInfo info,
        StringBuilder bodyBuilder)
    {
        // Stub routines (declared without a body, e.g. BuilderQuery.page_size()) get their
        // synthesized body from WiredRoutinePass via _synthesizedBodies. The parser produces an
        // empty BlockStatement for missing bodies, so check both null and empty.
        Statement? effectiveBody = routine.Body;
        bool isStubBody = effectiveBody is null
            || effectiveBody is BlockStatement { Statements.Count: 0 };
        if (isStubBody
            && _synthesizedBodies.TryGetValue(key: info.RegistryKey, value: out Statement? synthStub))
        {
            effectiveBody = synthStub;
        }
        if (effectiveBody != null)
        {
            GenerateRoutineBody(sb: bodyBuilder, body: effectiveBody, routine: info);
        }
        _functionDefinitions.Append(value: _currentRoutineEntryAllocas);
        _functionDefinitions.Append(value: bodyBuilder);
    }

    /// <summary>
    /// Generates code for a function body.
    /// Emits statements and ensures proper termination.
    /// </summary>
    private void GenerateRoutineBody(StringBuilder sb, Statement body, RoutineInfo routine)
    {
        // Mark that we are emitting a body: every routine declared (referenced) from here on is a
        // real callee of an emitted routine, so it must itself be emitted. Save/restore in case a
        // body's emission ever re-enters this memberRoutine.
        bool prevEmittingBody = _emittingRoutineBody;
        _emittingRoutineBody = true;
        try
        {
            EmitRoutineBodyInner(sb: sb, body: body, routine: routine);
        }
        finally
        {
            _emittingRoutineBody = prevEmittingBody;
        }
    }

    /// <summary>
    /// Resets per-routine state, binds <c>me</c> + parameters + closure captures, pushes a stack
    /// trace frame, emits the body statements and — if control falls off the end — the terminating
    /// zero-return / cleanup.
    /// </summary>
    private void EmitRoutineBodyInner(StringBuilder sb, Statement body, RoutineInfo routine)
    {
        ResetPerRoutineState(routine: routine);
        BindImplicitMeReceiver(sb: sb, body: body, routine: routine);
        RegisterParametersAsLocals(sb: sb, routine: routine);
        EmitClosurePrologue(sb: sb, routine: routine);
        EmitTracePush(sb: sb, routine: routine);

        // Emit the body statements — returns true if the block ends with a terminator
        if (EmitStatement(sb: sb, stmt: body))
        {
            return;
        }

        EmitFallthroughReturn(sb: sb, routine: routine);
    }

    /// <summary>Clears per-routine tracking sets and sets the current-routine context fields.</summary>
    private void ResetPerRoutineState(RoutineInfo routine)
    {
        _localVariables.Clear();
        _localVarLlvmNames.Clear();
        _varNameCounts.Clear();
        _localEntityVars.Clear();
        _cfNodes.Clear();
        _localRcRecordVars.Clear();
        _localRetainedVars.Clear();
        _currentRoutineEntryAllocas.Clear();
        _emittedAllocaNames.Clear();

        // Set current function return type for use in EmitReturn
        _currentRoutineReturnType = routine.ReturnType;
        _currentRoutineIsFailable = routine.IsFailable;

        // Track current routine for source_routine() / source_module() injection
        _currentEmittingRoutine = routine;
    }

    /// <summary>
    /// Binds the implicit <c>me</c> local for a memberRoutine, or — for an entity <c>create</c> that
    /// references <c>me</c> — allocates the entity at entry and binds <c>me</c> to the fresh pointer.
    /// </summary>
    private void BindImplicitMeReceiver(StringBuilder sb, Statement body, RoutineInfo routine)
    {
        // Register implicit 'me' for memberRoutines (skip create static factories and common routines).
        if (routine.OwnerType != null && !IsCreatorRoutine(routine: routine) && !routine.IsCommon)
        {
            BindMemberRoutineMeReceiver(sb: sb, routine: routine);
            return;
        }

        // Entity create that references `me`: allocate the entity at routine entry, bind `me` to the
        // fresh pointer, and let the body mutate via `me.field = …` / `return me`. Canonical
        // `return Type(field: …)` creates that never touch `me` skip this.
        if (routine.OwnerType is EntityTypeInfo creatorEntity &&
            IsCreatorRoutine(routine: routine) &&
            MeReferenceScanner.Scan(body: body))
        {
            string mePtr = EmitEntityAllocation(sb: sb, entity: creatorEntity);
            EmitEntryAlloca(llvmName: "%me.addr", llvmType: "ptr");
            EmitLine(sb: sb, line: $"  store ptr {mePtr}, ptr %me.addr");
            _localVariables[key: "me"] = routine.OwnerType;
        }
    }

    /// <summary>Binds <c>me</c> for a memberRoutine receiver (by-ref, void, or value-copy forms).</summary>
    private void BindMemberRoutineMeReceiver(StringBuilder sb, RoutineInfo routine)
    {
        // A Suflae entity member routine receives `me` as the `Roamed[E]` handle (SF slice 2 sets
        // MeType), so bind the local to that handle type — otherwise `me.field` codegen sees the
        // bare entity and reads the RC controller's refcount instead of dereferencing the handle.
        // Gated to a Roamed MeType so the specialized-receiver MeType path (List[Agent[V]]) is
        // untouched. The LLVM param type is a `ptr` for both, so only the tracked type changes.
        TypeInfo meLocalType =
            routine.MeType is RecordTypeInfo { GenericDefinition.Name: Resolution.RuntimeContract.Roamed }
                ? routine.MeType
                : routine.OwnerType!;

        // Struct-record `me` passed by reference: %me.addr IS the function parameter (the caller's
        // storage pointer). No alloca/store — mutations and address-taking reach it directly.
        if (IsByRefMeReceiver(routine: routine))
        {
            _localVariables[key: "me"] = meLocalType;
            return;
        }

        string meType = GetParameterLlvmType(type: routine.OwnerType!);
        // Skip alloca/store for void me (None owner type — unit type, no data)
        if (meType != "void")
        {
            EmitEntryAlloca(llvmName: "%me.addr", llvmType: meType);
            EmitLine(sb: sb, line: $"  store {meType} %me, ptr %me.addr");
        }
        _localVariables[key: "me"] = meLocalType;
    }

    /// <summary>Registers each parameter as a local variable, emitting its entry alloca/store.</summary>
    private void RegisterParametersAsLocals(StringBuilder sb, RoutineInfo routine)
    {
        foreach (ParameterInfo param in routine.Parameters)
        {
            RegisterParameterAsLocal(sb: sb, routine: routine, param: param);
        }
    }

    /// <summary>Registers a single parameter as a local variable per its ABI passing mode.</summary>
    private void RegisterParameterAsLocal(StringBuilder sb, RoutineInfo routine, ParameterInfo param)
    {
        // By-ref struct-record thread arg / ABI-Indirect byval param: `%<name>.addr` IS the
        // parameter (a pointer to the caller's / callee's copy). No alloca/store — field/memberRoutine
        // access resolves through the address directly.
        if (IsByRefThreadArg(routine: routine, param: param) ||
            ParameterPassedByval(routine: routine, paramType: param.Type))
        {
            _localVariables[key: param.Name] = param.Type;
            return;
        }

        // Use "entry_" instead of "entry" to avoid conflict with the entry: block label
        string emittedParamName = param.Name == "entry" ? "entry_" : param.Name;
        string paramPtr = $"%{param.Name}.addr";

        // ABI-Coerce small struct value param: the parameter arrives as an integer register value.
        // Allocate a slot of the ABI type (≥ the struct), store the register value into it, and bind
        // `%<name>.addr` to it — field access then reads the struct out. (records only)
        string? coerceType = ParameterCoerceType(routine: routine, paramType: param.Type);
        string storeType = coerceType ?? GetLlvmType(type: param.Type);
        EmitEntryAlloca(llvmName: paramPtr, llvmType: storeType);
        EmitLine(sb: sb, line: $"  store {storeType} %{emittedParamName}, ptr {paramPtr}");
        _localVariables[key: param.Name] = param.Type;

        // A bound-entity parameter is a consuming parameter: ownership was transferred in via
        // `steal` at the call site, so this routine is the new sole owner and must tear it down at
        // scope exit, exactly like a local entity `var`. Borrows arrive as wrappers (RecordTypeInfo),
        // never bare EntityTypeInfo, so they are correctly excluded. (ABI-Coerce is records only, so
        // never an entity — the coerce branch never reaches here.)
        if (coerceType == null && param.Type is EntityTypeInfo)
        {
            _localEntityVars.Add(item: (param.Name, paramPtr));
        }
    }

    /// <summary>
    /// Closure prologue (v0.4.1): loads each captured value out of the trailing bound payload (the
    /// hidden `ptr %__bound` parameter) into a local. The bound layout is `{ capture0, capture1, … }`
    /// (pure captures, NO leading fn pointer); captures start at field 0.
    /// </summary>
    private void EmitClosurePrologue(StringBuilder sb, RoutineInfo routine)
    {
        if (!routine.IsLambda || routine.ClosureCaptures is not { Count: > 0 } closureCaptures)
        {
            return;
        }

        string boundStruct = ClosureStructName(lambda: routine);
        for (int i = 0; i < closureCaptures.Count; i++)
        {
            (string capName, TypeInfo capType) = closureCaptures[index: i];
            string capLlvm = GetLlvmType(type: capType);
            string capPtr = NextTemp();
            EmitLine(sb: sb,
                line: $"  {capPtr} = getelementptr {boundStruct}, ptr %__bound, i32 0, i32 {i}");
            string capVal = NextTemp();
            EmitLine(sb: sb, line: $"  {capVal} = load {capLlvm}, ptr {capPtr}");
            string capAddr = $"%{capName}.addr";
            EmitEntryAlloca(llvmName: capAddr, llvmType: capLlvm);
            EmitLine(sb: sb, line: $"  store {capLlvm} {capVal}, ptr {capAddr}");
            _localVariables[key: capName] = capType;
        }
    }

    /// <summary>
    /// Emits the stack-trace push (and records whether to emit the matching pop). Synthesized
    /// routines, and @inline helpers in Release, are skipped.
    /// </summary>
    private void EmitTracePush(StringBuilder sb, RoutineInfo routine)
    {
        bool isInline = routine.Annotations.Contains(value: "inline");
        _traceCurrentRoutine = ShouldEmitTrace &&
                               !routine.IsSynthesized &&
                               !(_buildMode is RfBuildMode.Release && isInline);
        if (!_traceCurrentRoutine)
        {
            return;
        }

        string paramTypes = string.Join(separator: ", ",
            values: routine.Parameters.Select(selector: p => p.Type.FullName));
        string failable = routine.IsFailable ? "!" : "";
        string routineName = $"{routine.BaseName}{failable}({paramTypes})";
        string fileName = routine.Location?.FileName ?? "";
        int line = routine.Location?.Line ?? 0;
        int col = routine.Location?.Column ?? 0;
        string routineCStr = EmitCStringConstant(value: routineName);
        string fileCStr = EmitCStringConstant(value: fileName);
        EmitLine(sb: sb,
            line: $"  call void @_rf_trace_push(ptr {routineCStr}, ptr {fileCStr}, i32 {line}, i32 {col})");
    }

    /// <summary>
    /// Emits the implicit terminator when the body falls off the end: RC/entity cleanup, the trace
    /// pop, and a zero-value return in the routine's ABI return form (void / sret / coerced / value).
    /// </summary>
    private void EmitFallthroughReturn(StringBuilder sb, RoutineInfo routine)
    {
        EmitRcRecordCleanup(sb: sb);
        EmitEntityCleanup(sb: sb, returnedVarName: null);
        if (_traceCurrentRoutine)
        {
            EmitLine(sb: sb, line: "  call void @_rf_trace_pop()");
        }

        string retType = routine.ReturnType != null
            ? GetLlvmType(type: routine.ReturnType)
            : "void";
        if (retType == "void")
        {
            EmitVoidFallthroughReturn(sb: sb, routine: routine);
            return;
        }
        if (_currentReturnViaSret)
        {
            // Indirect (sret) return: the header is void, so store the zero struct through the
            // hidden %sret pointer and return void.
            string zeroValue = GetZeroValue(type: routine.ReturnType!);
            EmitLine(sb: sb, line: $"  store {retType} {zeroValue}, ptr %sret");
            EmitLine(sb: sb, line: "  ret void");
            return;
        }
        if (_currentReturnCoerceType != null)
        {
            // Coerced (Phase 2) return: the header returns the ABI integer type; zero fills it.
            EmitLine(sb: sb, line: $"  ret {_currentReturnCoerceType} zeroinitializer");
            return;
        }
        EmitLine(sb: sb, line: $"  ret {retType} {GetZeroValue(type: routine.ReturnType!)}");
    }

    /// <summary>
    /// Emits the fallthrough return for a void return type — the failable-variant carrier form for
    /// check_/try_ wrappers, or plain <c>ret void</c>.
    /// </summary>
    private void EmitVoidFallthroughReturn(StringBuilder sb, RoutineInfo routine)
    {
        switch (routine.FailableVariant)
        {
            case FailableVariant.Check:
                string carrier = GetResultCarrierLlvmType(valueType: routine.ReturnType!);
                EmitLine(sb: sb, line: $"  ret {carrier} zeroinitializer");
                break;
            case FailableVariant.TryBool:
                EmitLine(sb: sb, line: "  ret i1 false");
                break;
            default:
                EmitLine(sb: sb, line: "  ret void");
                break;
        }
    }

    /// <summary>
    /// Mangles a function name to be LLVM-compatible.
    /// </summary>
    internal static string MangleRoutineName(RoutineInfo routine)
    {
        // All routines with parameters are disambiguated by parameter type. Overloads
        // sharing only a name (e.g. LocalMoment.sub(Duration) vs sub(LocalMoment),
        // or hash() vs hash(k0, k1)) collapse to the same symbol otherwise and the
        // linker arbitrarily picks one definition, mis-typing every call site.
        static bool ShouldDisambiguateByParameterTypes(RoutineInfo candidate) =>
            candidate.Parameters.Count > 0;

        // Failability is a routine PROPERTY (IsFailable), never part of the symbol name. The `!`
        // is stripped from every mangled symbol — `foo()` and `foo!()` with the same params are a
        // duplication error (RegistryKey excludes failability), so `owner.name(params)` is already
        // a unique symbol and the bang would only be decorative. Kept as a no-op wrapper so the
        // owner-case call sites below read uniformly.
        static string Bang(string name, bool failable) => name;

        // Structured attribute prefix — the routine's PROPERTIES (kind, wired-ness, failability,
        // async mode, storage) are obfuscated into a bracketed list so the name itself carries only
        // the module-qualified raw identifier. E.g. `[member, wired] Core.Address.create(...)`,
        // `[independent, crashable] Foo.parse(...)`. External("C") routines are EXEMPT (they keep the
        // raw C symbol so the LLVM declare links against the native lib), so this is not called there.
        static string AttrPrefix(RoutineInfo r)
        {
            var attrs = new List<string> { r.OwnerType != null ? "member" : "independent" };
            if (r.IsCommon) attrs.Add(item: "common");
            if (r.IsWiredMemberRoutine) attrs.Add(item: "wired");
            if (r.IsFailable) attrs.Add(item: "crashable");
            if (r.IsDangerous) attrs.Add(item: "dangerous");
            // Visibility is an attribute too. A member of a `secret` (module-private) type is itself
            // module-private regardless of its own modifier (owner-secrecy cap), so decorate `secret`
            // when EITHER the routine or its owner type is secret. `open` is the default → not emitted.
            // (`posted` is member-variable-only — routines are only secret/open/external.)
            bool ownerSecret = r.OwnerType is { Visibility: VisibilityModifier.Secret };
            if (r.Visibility == VisibilityModifier.Secret || ownerSecret) attrs.Add(item: "secret");
            if (r.IsSuspended) attrs.Add(item: "suspended");
            else if (r.IsThreaded) attrs.Add(item: "threaded");
            attrs.Sort();
            return $"[{string.Join(separator: ", ", values: attrs)}] ";
        }

        // Labeled parameter list — `(label: Core.Type, …)` — the label participates in overload
        // identity (RazorForge dispatches on named args), so it belongs in the mangled symbol.
        static string LabeledParams(RoutineInfo r) =>
            "(" + string.Join(separator: ", ",
                values: r.Parameters.Select(selector: p => $"{p.Name}: {p.Type.FullName}")) + ")";

        // Lambda closures: [lambda]filename:line:col!(paramTypes)
        if (routine.IsLambda)
        {
            string fileName =
                Path.GetFileName(path: routine.Location?.FileName ?? "[unknown]");
            int line = routine.Location?.Line ?? 0;
            int col = routine.Location?.Column ?? 0;
            string paramTypes = string.Join(separator: ",",
                values: routine.Parameters.Select(selector: p => p.Type.Name));
            string lambdaName = Bang(name: $"[lambda]{fileName}:{line}:{col}", failable: routine.IsFailable);
            return Q(name: $"{lambdaName}({paramTypes})");
        }

        // External("C") functions use the raw C symbol name — no module prefix, so that LLVM IR symbols
        // match the actual C linker symbols. `@link(..., symbol: "x")` overrides the linked symbol when the
        // RF-side name differs (versioned symbols, stat→stat64, decorated names). Declaration and call site
        // both mangle from the same RoutineInfo, so they agree on the override.
        if (routine.CallingConvention == "C")
        {
            return Q(name: Bang(name: SanitizeLlvmName(name: routine.LinkSymbol is { Length: > 0 } sym ? sym : routine.Name),
                failable: routine.IsFailable));
        }

        string name = SanitizeLlvmName(name: routine.Name);
        if (routine.OwnerType == null)
        {
            // Top-level: `[independent, …] Module.name(typeargs)(label: Type, …)`.
            // BaseName preserves the module-qualified form; attributes + labeled params carry the
            // former `!`/`$`/decoration.
            string fullName = AttrPrefix(r: routine) + SanitizeLlvmName(name: routine.BaseName);

            // Generic instance: append type arguments (e.g., IO.show -> IO.show#S64)
            if (routine.TypeArguments is { Count: > 0 })
            {
                string typeArgSuffix = string.Join(separator: ",",
                    values: routine.TypeArguments.Select(selector: t => t.Name));
                // Disambiguate variadic overloads (e.g., show...#Text vs show#Text)
                string variadicMarker = routine.IsVariadic
                    ? "..."
                    : "";
                fullName = $"{fullName}({typeArgSuffix}{variadicMarker})";
            }

            fullName += LabeledParams(r: routine);
            return Q(name: fullName);
        }

        // Common (type-level static) routines: `[member, common, …] Module.Type.name(label: Type, …)`.
        if (routine.IsCommon)
        {
            string typeName = RealmMangleBase(t: routine.OwnerType);
            return Q(name: $"{AttrPrefix(r: routine)}{typeName}.{name}{LabeledParams(r: routine)}");
        }

        // memberRoutine: `[member, wired?, crashable?, …] Module.OwnerType.name(label: Type, …)`
        // (OwnerType.FullName includes module). The `$`/`!` are gone from the name — they are in the
        // attribute prefix.
        string ownerTypeName = RealmMangleBase(t: routine.OwnerType);
        string baseName = AttrPrefix(r: routine) + $"{ownerTypeName}.{name}";

        // memberRoutine-level type arguments (e.g., Hijacked[U64].recast_as[BTreeListNode[S64]]).
        // Distinct from owner type args already in OwnerType.FullName.
        if (routine.TypeArguments is { Count: > 0 } memberRoutineTypeArgs)
        {
            // Only include type args that aren't already in the owner's type arg list to
            // avoid duplicating owner generics (memberRoutine.TypeArguments may be a superset).
            // Also drop bare GenericParameterTypeInfo entries: when SignatureResolver leaves
            // an owner-level param (e.g. T) in routine.TypeArguments and the owner is already
            // monomorphized to a concrete type, the bare T would survive the name-based
            // dedup and mangle as `Type[Concrete].MemberRoutine[T]` — producing an undefined symbol.
            //
            // Additionally drop entries whose Name matches one of the owner gen-def's
            // GenericParameters (e.g. "T" for SortedSet[T]). When TransferSubstitutedTypeArguments
            // or SubstituteMemberRoutineForOwner forwards a stale leftover TypeInfo named "T" that is
            // *not* a GenericParameterTypeInfo (some passes wrap the owner-leak in a non-GPTI),
            // the GPTI check above doesn't catch it. Matching by name closes that hole.
            var ownerArgs = routine.OwnerType.TypeArguments ?? [];
            TypeInfo? ownerGenDef = routine.OwnerType switch
            {
                RecordTypeInfo r => r.GenericDefinition ?? r,
                EntityTypeInfo e => e.GenericDefinition ?? e,
                ProtocolTypeInfo p => p.GenericDefinition ?? p,
                _ => routine.OwnerType
            };
            List<string> ownerGenDefParamNames = ownerGenDef?.GenericParameters ?? [];
            var memberRoutineOnlyArgs = memberRoutineTypeArgs
                .Where(predicate: a => a is not GenericParameterTypeInfo
                    && !ownerArgs.Any(predicate: o => o.FullName == a.FullName)
                    && !ownerGenDefParamNames.Contains(item: a.Name))
                .ToList();
            if (memberRoutineOnlyArgs.Count > 0)
            {
                string typeArgSuffix = string.Join(separator: ",",
                    values: memberRoutineOnlyArgs.Select(selector: t => t.FullName));
                baseName = $"{baseName}[{typeArgSuffix}]";
            }
        }

        // Labeled parameter list — `(label: Type, …)` — always appended (even empty `()`); the label
        // is part of overload identity. Uses MangleParamTypeName for wrapper-forwarder inner-generic
        // param mapping.
        baseName += "(" + string.Join(separator: ", ",
            values: routine.Parameters.Select(
                selector: p => $"{p.Name}: {MangleParamTypeName(routine: routine, paramType: p.Type)}")) + ")";

        return Q(name: baseName);
    }

    /// <summary>
    /// Maps a wrapper-forwarder's disambiguated inner generic param (e.g. `__rfwd_T__`,
    /// or the original `T` colliding with the wrapper's own param) to the concrete inner
    /// type argument for symbol mangling. Without this, a forwarder body for
    /// `Retained[ListNode[S64]].chain_contains(T)` emits as `chain_contains(__rfwd_T__)`
    /// while the rewritten call site emits `chain_contains(Core.S64)` — linker miss.
    /// </summary>
    private static string MangleParamTypeName(RoutineInfo routine, TypeInfo paramType)
    {
        if (routine.WrapperForwarderInnerGenericDef?.GenericParameters
                is { Count: > 0 } innerParamNames
            && routine.OwnerType?.TypeArguments is { Count: > 0 and 1 } ownerArgs
            && ownerArgs[index: 0].TypeArguments is { } innerArgs
            && innerArgs.Count == innerParamNames.Count)
        {
            return MangleParamTypeFullName(type: paramType,
                innerParamNames: innerParamNames, innerArgs: innerArgs);
        }
        return paramType.FullName;
    }

    private static string MangleParamTypeFullName(TypeInfo type,
        List<string> innerParamNames, List<TypeInfo> innerArgs)
    {
        if (type is GenericParameterTypeInfo gp)
        {
            string lookup = gp.ForwarderOriginalName ?? gp.Name;
            int idx = innerParamNames.IndexOf(item: lookup);
            if (idx < 0) idx = innerParamNames.IndexOf(item: gp.Name);
            return idx >= 0 ? innerArgs[index: idx].FullName : type.FullName;
        }
        if (type.TypeArguments is { Count: > 0 } args)
        {
            string baseName = string.IsNullOrEmpty(value: type.Module)
                ? type.Name
                : $"{type.Module}.{type.Name}";
            if (type.Name.Contains(value: '['))
            {
                // Name already embeds the (unsubstituted) args. Substitute literally inside it
                // is brittle; fall back to per-arg recursion for the bracket portion.
                baseName = string.IsNullOrEmpty(value: type.Module)
                    ? type.BareName
                    : $"{type.Module}.{type.BareName}";
            }
            string joined = string.Join(separator: ", ",
                values: args.Select(selector: a => MangleParamTypeFullName(
                    type: a, innerParamNames: innerParamNames, innerArgs: innerArgs)));
            return $"{baseName}[{joined}]";
        }
        return type.FullName;
    }

    // Failability is a routine PROPERTY, never part of the symbol — the `!` is not appended.
    // (Retained as a pass-through for the unresolved/C-call fallback path so its call sites read
    // uniformly with MangleRoutineName, which also strips the bang.)
    internal static string DecorateRoutineSymbolName(string baseName, bool isFailable)
    {
        return baseName;
    }

    /// <summary>
    /// Sanitizes a name for use as an LLVM IR identifier.
    /// Replaces characters that are invalid in LLVM identifiers.
    /// </summary>
    internal static string SanitizeLlvmName(string name)
    {
        return name.Replace(oldValue: "!", newValue: "");
    }

    private static bool IsCreatorRoutine(RoutineInfo routine)
    {
        return routine.Name.Contains(value: "create") ||
               routine.Name is "try_create" or "check_create" or "lookup_create" ||
               routine.OriginalName?.Contains(value: "create") == true;
    }

    private string GetImplicitMeParameterDeclaration(RoutineInfo routine, bool includeName)
    {
        if (routine.OwnerType == null)
        {
            throw new InvalidOperationException(message: "Implicit 'me' requested for routine without owner type.");
        }

        if (IsByRefMeReceiver(routine: routine))
        {
            // Struct-record `me` is a pointer to the caller's storage (named %me.addr), so the
            // parameter doubles as the field-access base — no alloca/store prologue needed.
            string nameSuffix = includeName ? " %me.addr" : string.Empty;
            return $"ptr{nameSuffix}";
        }

        string meType = GetParameterLlvmType(type: routine.OwnerType);
        string attrs = GetImplicitMeParameterAttributes(routine: routine);
        string nameSuffix2 = includeName ? " %me" : string.Empty;

        return string.IsNullOrEmpty(value: attrs)
            ? $"{meType}{nameSuffix2}"
            : $"{meType} {attrs}{nameSuffix2}";
    }

    private static string GetImplicitMeParameterAttributes(RoutineInfo routine)
    {
        // Exclusive me-params get `noalias`. Two cases qualify:
        //   - bare entity (bound T can't be duplicated, so the me pointer is exclusive
        //     at the call boundary by the entity-ownership rule),
        //   - `Modifying[T]` (scope-bound exclusive borrow — its definition).
        bool isExclusive = routine.OwnerType is EntityTypeInfo
                           || routine.OwnerType is WrapperTypeInfo { Name: Resolution.RuntimeContract.Modifying };
        if (isExclusive)
        {
            return routine.MutationCategory == MutationCategory.Readonly
                ? "noalias readonly"
                : "noalias";
        }

        if (routine.MutationCategory != MutationCategory.Readonly)
        {
            return string.Empty;
        }

        // A @readonly method on a wrapper has a pointer `me` it does not write — mark it `readonly`.
        // Recognise BOTH representations of a wrapper owner: the generic is a WrapperTypeInfo, but a
        // MONOMORPHIZED wrapper is a RecordTypeInfo (e.g. Hijacked[Byte]) matched by its base name in
        // RuntimeContract.WrapperTypes. The two must emit the SAME attr or the cold vs warm/snapshot
        // codegen paths diverge (the monomorph reaches codegen in one path, the generic in the other) —
        // WarmCodegenAst_MatchesCold.
        bool isWrapperOwner = routine.OwnerType is WrapperTypeInfo
            || (GetGenericBaseNameStatic(type: routine.OwnerType) is { } ownerBase
                && Resolution.RuntimeContract.WrapperTypes.Contains(item: ownerBase));
        return isWrapperOwner ? "readonly" : string.Empty;
    }

    private static string GetExplicitParameterAttributes(TypeInfo? type) =>
        type is EntityTypeInfo
        || type is WrapperTypeInfo { Name: Resolution.RuntimeContract.Modifying }
            ? "noalias"
            : string.Empty;

    /// <summary>
    /// Checks if an external("C") function returns a struct type that must be called with an
    /// explicit sret pointer to match the platform C ABI.
    /// Win64 (MSVC): aggregates larger than 8 bytes return via a hidden sret pointer.
    /// SysV x86-64 / AAPCS64: aggregates up to 16 bytes return in registers (RAX:RDX / x0:x1),
    /// which LLVM's natural aggregate-return lowering already matches — forcing sret there
    /// shifts every C argument by one slot and leaves the result alloca unwritten
    /// (the Linux-CI D128 "1/3 = garbage" bug); only >16-byte aggregates go through memory.
    /// </summary>
    private bool NeedsCExternSret(RoutineInfo routine)
    {
        if (routine.ReturnType == null)
        {
            return false;
        }

        string llvmType = GetLlvmType(type: routine.ReturnType);
        if (!llvmType.StartsWith(value: "%Record.") && !llvmType.StartsWith(value: "%\"Record.") &&
            !llvmType.StartsWith(value: "%Tuple.") && !llvmType.StartsWith(value: "%\"Tuple."))
        {
            return false;
        }

        int size = GetTypeSize(type: routine.ReturnType);
        return _target.TargetOS == "windows" ? size > 8 : size > 16;
    }

    /// <summary>
    /// Whether a record's <c>me</c> is passed by reference (a <c>ptr</c> to the caller's storage)
    /// rather than by value. This is a purely type-level decision — no per-memberRoutine special cases:
    /// <list type="bullet">
    /// <item><b>By reference</b> — every <i>storage-backed</i> record: a struct record (no
    /// <c>@llvm</c> backend) or an <c>@llvm</c> record whose backend is an <i>aggregate</i>
    /// (<c>[N x T]</c>, i.e. <c>Array[T,N]</c> / <c>BitArray[N]</c>). By-ref lets any memberRoutine mutate
    /// in place and take stable addresses (hijack/get_address, atomics, C FFI), and avoids copying
    /// the aggregate on every call.</item>
    /// <item><b>By value</b> — only <i>scalar</i> <c>@llvm</c> records (<c>iN</c>, <c>fN</c>,
    /// <c>ptr</c>: numerics, <c>Bool</c>, <c>Hijacked</c>, …). The value <i>is</i> the machine
    /// register their operators feed to LLVM intrinsics (<c>add i64 %me, %you</c>), so a pointer
    /// would be wrong. These are pure values and never mutate <c>me</c> in place, so "needs by-value"
    /// and "mutates in place" never overlap.</item>
    /// </list>
    /// Entities are already by-ref via their pointer ABI. This replaces the old <c>setitem</c>
    /// name-check: <c>Array.setitem</c> is by-ref because Array is aggregate-backed, like every
    /// other Array memberRoutine — not because of its name.
    /// </summary>
    internal static bool IsByRefMeRecord(TypeInfo? ownerType) => ownerType switch
    {
        // Struct record: no @llvm backend -> storage-backed -> by-ref.
        RecordTypeInfo { HasDirectBackendType: false } => true,
        // @llvm record: by-ref iff the backend is an aggregate — an array `[N x T]` or a SIMD
        // vector `<N x E>`. Both are always accessed through a load/store (never fed to an
        // intrinsic as a bare SSA value like a scalar `i64`), and both need in-place `setitem!`
        // to reach the caller's storage. Scalar backends (`i64`, `i1`, `ptr`, ...) stay by-value.
        RecordTypeInfo { HasDirectBackendType: true, BackendType: { } bt } => bt.StartsWith(value: '[') || bt.StartsWith(value: '<'),
        _ => false
    };

    private static bool IsByRefMeReceiver(RoutineInfo routine) =>
        IsByRefMeRecord(ownerType: routine.OwnerType);

    /// <summary>
    /// A <b>thread-shareable</b> record argument to a <c>threaded routine</c> is passed BY
    /// REFERENCE: the worker's parameter is a pointer to the spawner's storage, so every worker
    /// that receives the same cell operates on one address (the basis of <c>Atomic[T]</c>
    /// cross-thread sharing). This mirrors the by-ref <c>me</c> convention — the parameter doubles
    /// as the field/memberRoutine-access base, no alloca/store copy.
    /// <para>
    /// Only types that carry their own synchronization (<c>Atomic</c>/<c>Guarded</c>/<c>Witnessed</c>)
    /// are shared this way. Every OTHER record falls through to the normal by-value parameter path
    /// (an independent copy is materialised in the worker's prologue), so unsynchronized state can
    /// never silently alias across the thread boundary. Plain scalar value types
    /// (numerics, <c>Hijacked</c>, ...) were always by value. SA (RF-S632) rejects by-ref records
    /// that are neither shareable nor trivially copyable, so they never reach codegen.
    /// </para>
    /// </summary>
    private static bool IsByRefThreadArg(RoutineInfo routine, ParameterInfo param) =>
        routine.AsyncStatus == AsyncStatus.Threaded &&
        IsByRefMeRecord(ownerType: param.Type) &&
        IsThreadShareableType(type: param.Type);

    /// <summary>
    /// True when a type carries its own cross-thread synchronization — the atomic / shared-ownership
    /// wrappers <c>Atomic[T]</c>, <c>Guarded[T,P]</c>, <c>Witnessed[T,P]</c>. These may be passed by
    /// reference across a thread boundary; everything else is copied. Mirrors the SA-side
    /// <c>IsThreadShareable</c>.
    /// </summary>
    private static bool IsThreadShareableType(TypeInfo? type) =>
        type != null &&
        GetGenericBaseNameStatic(type: type) is Resolution.RuntimeContract.Atomic or Resolution.RuntimeContract.Guarded or Resolution.RuntimeContract.Witnessed;

    /// <summary>
    /// Gets the zero/default value for a type.
    /// </summary>
    private static string GetZeroValue(TypeInfo type)
    {
        return type switch
        {
            RecordTypeInfo { HasDirectBackendType: true } record => GetZeroValueForLlvmType(
                llvmType: record.BackendType!),
            EntityTypeInfo or WrapperTypeInfo => "null",
            _ => "zeroinitializer"
        };
    }

    /// <summary>
    /// Gets the zero value for an LLVM type string (from @llvm annotation).
    /// </summary>
    private static string GetZeroValueForLlvmType(string llvmType)
    {
        return llvmType switch
        {
            "i1" => "false",
            "half" or "float" or "double" or "fp128" => "0.0",
            "ptr" => "null",
            // Aggregate types ([N x T] arrays, {...} structs, %"Named" struct types) require
            // `zeroinitializer`, not the integer literal `0`. Without this, BitArray[N]() and
            // similar no-arg constructors emit `store [1 x i8] 0, ...` which opt rejects with
            // "integer constant must have integer type".
            _ when llvmType.Length > 0 &&
                   (llvmType[0] == '[' || llvmType[0] == '{' || llvmType[0] == '%') => "zeroinitializer",
            _ => "0"
        };
    }

}
