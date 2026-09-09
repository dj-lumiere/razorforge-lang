using System.Text;
using SyntaxTree;
using TypeModel.Types;

namespace Compiler.CodeGen;

/// <summary>
/// Statement code generation for return, throw, absent, and variant-return paths.
/// </summary>
public partial class LlvmCodeGenerator
{
    private const string TracePop = "  call void @_rf_trace_pop()";
    private const string RetVoid = "  ret void";

    #region Return Statements

    private void EmitReturn(StringBuilder sb, ReturnStatement ret)
    {
        if (ret.Value == null)
        {
            EmitNullValueReturn(sb: sb);
            return;
        }

        string earlyType = _currentRoutineReturnType != null
            ? GetLlvmType(type: _currentRoutineReturnType)
            : "void";
        if (earlyType == "void")
        {
            // A `return <expr>` in a None-returning routine still EVALUATES a side-effecting <expr>
            // for its effects — only the RESULT is dropped. Skipping this silently swallows a crash:
            // `routine f!()` with `return a // b` (b == 0) desugars to `-> None` + `return
            // a.floordiv(b)`, and the failing division must still fire. After lowering, side effects
            // live in calls (checked arithmetic, failable/floordiv, etc.); a bare `None`/identifier/
            // literal has no effect to preserve and `None` is not an emittable value, so evaluate
            // only a call. The SSA result is discarded.
            if (ret.Value is CallExpression)
            {
                EmitExpression(sb: sb, expr: ret.Value);
            }

            EmitNoneExpressionReturn(sb: sb);
            return;
        }

        TypeInfo? retValType = GetExpressionType(expr: ret.Value);
        if (retValType is CrashableTypeInfo && _currentRoutineIsFailable)
        {
            EmitThrow(sb: sb, throwStmt: new ThrowStatement(Error: ret.Value, Location: ret.Location));
            return;
        }

        EmitValueReturn(sb: sb, ret: ret);
    }

    /// <summary>Emits the IR for a return that carries a non-void, non-crashable value.</summary>
    private void EmitValueReturn(StringBuilder sb, ReturnStatement ret)
    {
        string value = EmitExpression(sb: sb, expr: ret.Value!);
        TypeInfo? retType = _currentRoutineReturnType ?? GetExpressionType(expr: ret.Value!);
        if (retType == null)
            throw new InvalidOperationException(message: "Cannot determine return type for return statement");

        string llvmType = GetLlvmType(type: retType);

        string? returnedVarName = ret.Value is IdentifierExpression id &&
                                  _localEntityVars.Any(predicate: e => e.Name == id.Name)
            ? id.Name
            : null;
        EmitRcRecordCleanup(sb: sb);
        EmitEntityCleanup(sb: sb, returnedVarName: returnedVarName);
        if (_traceCurrentRoutine)
            EmitLine(sb: sb, line: TracePop);

        TypeInfo? exprType = GetExpressionType(expr: ret.Value!);
        if (IsMaybeType(type: retType) && value != "zeroinitializer" &&
            (exprType == null || !IsMaybeType(type: exprType)))
        {
            EmitMaybeWrappedReturn(sb: sb, retType: retType, innerValue: value);
            return;
        }

        // Indirect (sret) return: the struct value is stored through the hidden %sret pointer and
        // the function returns void (see _currentReturnViaSret / GenerateRoutineDefinition).
        if (_currentReturnViaSret)
        {
            EmitLine(sb: sb, line: $"  store {llvmType} {value}, ptr %sret");
            EmitLine(sb: sb, line: RetVoid);
            return;
        }
        // Coerced (Phase 2) return: reinterpret the struct value into its ABI register type.
        if (_currentReturnCoerceType != null)
        {
            string coerced = CoerceStructToAbi(sb: sb, structValue: value, structLlvm: llvmType,
                abiType: _currentReturnCoerceType);
            EmitLine(sb: sb, line: $"  ret {_currentReturnCoerceType} {coerced}");
            return;
        }
        EmitLine(sb: sb, line: $"  ret {llvmType} {value}");
    }

    private void EmitNullValueReturn(StringBuilder sb)
    {
        EmitRcRecordCleanup(sb: sb);
        EmitEntityCleanup(sb: sb, returnedVarName: null);
        if (_traceCurrentRoutine)
            EmitLine(sb: sb, line: TracePop);
        if (_currentRoutineReturnType == null)
        {
            EmitLine(sb: sb, line: RetVoid);
            return;
        }
        string retLlvmType = GetLlvmType(type: _currentRoutineReturnType);
        if (retLlvmType == "void")
            EmitLine(sb: sb, line: RetVoid);
        else
        {
            string retZero = GetZeroValue(type: _currentRoutineReturnType);
            EmitLine(sb: sb, line: $"  ret {retLlvmType} {retZero}");
        }
    }

    // For check_/try_ variant wrappers with None (void) return, emit success carrier.
    private void EmitNoneExpressionReturn(StringBuilder sb)
    {
        EmitRcRecordCleanup(sb: sb);
        EmitEntityCleanup(sb: sb, returnedVarName: null);
        if (_traceCurrentRoutine)
            EmitLine(sb: sb, line: TracePop);
        if (_currentEmittingRoutine?.FailableVariant == FailableVariant.Check &&
            _currentRoutineReturnType != null)
        {
            string carrier = GetResultCarrierLlvmType(valueType: _currentRoutineReturnType);
            EmitLine(sb: sb, line: $"  ret {carrier} zeroinitializer");
        }
        else if (_currentEmittingRoutine?.FailableVariant == FailableVariant.TryBool)
        {
            EmitLine(sb: sb, line: "  ret i1 false");
        }
        else
        {
            EmitLine(sb: sb, line: RetVoid);
        }
    }

    private void EmitMaybeWrappedReturn(StringBuilder sb, TypeInfo retType, string innerValue)
    {
        TypeInfo innerType = retType.TypeArguments is { Count: > 0 }
            ? retType.TypeArguments[index: 0]
            : retType;
        string carrierType = GetLlvmType(type: retType);
        string innerLlvm = innerType is EntityTypeInfo ? "ptr" : GetLlvmType(type: innerType);
        // Maybe `present` (field 0) is a Bool, stored as i8 (see GetFieldStorageLlvmType).
        string v0 = NextTemp();
        EmitLine(sb: sb, line: $"  {v0} = insertvalue {carrierType} zeroinitializer, i8 1, 0");
        string v1 = NextTemp();
        EmitLine(sb: sb, line: $"  {v1} = insertvalue {carrierType} {v0}, {innerLlvm} {innerValue}, 1");
        EmitLine(sb: sb, line: $"  ret {carrierType} {v1}");
    }

    private bool IsEntityConstructorCall(Expression? expr)
    {
        return expr switch
        {
            CreatorExpression { ConstructedType: EntityTypeInfo } or
                ListLiteralExpression or SetLiteralExpression
                or DictLiteralExpression => true,
            CreatorExpression => true,
            CallExpression { ConstructedType: EntityTypeInfo } => true,
            CallExpression { Callee: IdentifierExpression id } =>
                _registry.LookupType(name: id.Name) is EntityTypeInfo,
            _ => false
        };
    }

    private static void EmitEntityCleanup(StringBuilder sb, string? returnedVarName)
    {
        // Scope-exit teardown of owned locals is lowered into the AST as explicit
        // `local.destroy()` calls by ScopeTeardownLoweringPass (Phase 8), so codegen emits none.
        //
        // The entity self-free (freeing the heap allocation backing `me`) is ALSO lowered into the
        // synthesized `destroy` body as `me.hijack().invalidate()` (see
        // WiredRoutinePass.BuildEntitySelfFree). Codegen used to additionally emit a raw
        // `rf_invalidate(me)` here for synthesized entity `destroy`, but that DUPLICATED the
        // AST-level free → every synthesized entity `destroy` double-freed `me` (ASan: "double-free"
        // / glibc "double free in tcache"), crashing programs that destroy an owned entity at scope
        // exit (e.g. `using x.modify() as g`). The AST free is the single source of truth, so this is
        // now a no-op; the parameters are kept for call-site compatibility.
        _ = sb;
        _ = returnedVarName;
    }

    #endregion

    #region Throw / Absent / Becomes


    private void EmitThrow(StringBuilder sb, ThrowStatement throwStmt)
    {
        TypeInfo? errorType = GetExpressionType(expr: throwStmt.Error);
        string typeName = errorType?.Name ?? "UnknownError";

        bool isEmptyRecord = errorType is RecordTypeInfo { MemberVariables.Count: 0 };
        string errorVal;
        if (isEmptyRecord)
        {
            errorVal = "zeroinitializer";
        }
        else
        {
            errorVal = EmitExpression(sb: sb, expr: throwStmt.Error);
        }

        string dataPtr = "null";
        string msgLen = "0";
        ResolvedMemberRoutine? resolvedCrash = errorType != null
            ? ResolveMemberRoutine(receiverType: errorType, memberRoutineName: Declaration.RuntimeContract.CrashMessage)
            : null;
        if (resolvedCrash != null)
        {
            EmitCrashMessageText(sb: sb, resolvedCrash: resolvedCrash, errorType: errorType!,
                errorVal: errorVal, dataPtr: out dataPtr, msgLen: out msgLen);
        }

        string typeCStr = EmitCStringConstant(value: typeName);
        string fileCStr = EmitCStringConstant(value: throwStmt.Location.FileName);
        string typeNameAsInt = NextTemp();
        EmitLine(sb: sb, line: $"  {typeNameAsInt} = ptrtoint ptr {typeCStr} to i64");
        string fileAsInt = NextTemp();
        EmitLine(sb: sb, line: $"  {fileAsInt} = ptrtoint ptr {fileCStr} to i64");

        string msgDataAsInt;
        if (dataPtr == "null")
        {
            msgDataAsInt = "0";
        }
        else
        {
            msgDataAsInt = NextTemp();
            EmitLine(sb: sb, line: $"  {msgDataAsInt} = ptrtoint ptr {dataPtr} to i64");
        }

        EmitRcRecordCleanup(sb: sb);

        EmitLine(sb: sb,
            line:
            $"  call void @rf_crash(i64 {typeNameAsInt}, i64 {typeName.Length}, i64 {fileAsInt}, i64 {throwStmt.Location.FileName.Length}, i32 {throwStmt.Location.Line}, i32 {throwStmt.Location.Column}, i64 {msgDataAsInt}, i64 {msgLen})");
        EmitLine(sb: sb, line: "  unreachable");
    }

    /// <summary>
    /// Calls the error's <c>crash_message()</c> (sret- or by-value-returning a Text) and extracts the
    /// codepoint-buffer pointer and count into <paramref name="dataPtr"/> / <paramref name="msgLen"/>.
    /// </summary>
    private void EmitCrashMessageText(StringBuilder sb, ResolvedMemberRoutine resolvedCrash,
        TypeInfo errorType, string errorVal, out string dataPtr, out string msgLen)
    {
        GenerateRoutineDeclaration(routine: resolvedCrash.Routine);
        string mangledCrash = resolvedCrash.MangledName;
        string llvmReceiverType = GetLlvmType(type: errorType);

        // crash_message() returns a Text by value. Derive the Text record type AND the buffer/count
        // field indices from the registered Text type — never assume the physical field order.
        var textRecord = _registry.LookupType(name: "Text") as RecordTypeInfo
            ?? _registry.LookupType(name: "Core.Text") as RecordTypeInfo;
        string textLlvm = textRecord != null
            ? GetRecordTypeName(record: textRecord)
            : "%Record.Core.Text";
        int dataIdx = textRecord != null
            ? ResolveRecordFieldIndex(record: textRecord, memberVariableName: "data")
            : 0;
        int countIdx = textRecord != null
            ? ResolveRecordFieldIndex(record: textRecord, memberVariableName: "count")
            : 1;

        // crash_message() returns a Text (24 bytes) — ABI-Indirect on every target, so it comes
        // back through a hidden sret pointer (definition, declaration, and this call must all agree,
        // see ReturnsViaSret). Calling it with the by-value return ABI binds the receiver as the sret
        // result pointer and reads `me` from an uninitialized slot, producing a Text with a garbage
        // count — rf_crash then walks a wild UTF-32 range and the crash REPORT itself garbles or
        // AccessViolation-crashes. Mirror the sret call form the normal call path uses.
        string textVal = NextTemp();
        if (ReturnsViaSret(routine: resolvedCrash.Routine))
        {
            string sretPtr = NextTemp();
            EmitEntryAlloca(llvmName: sretPtr, llvmType: textLlvm);
            EmitLine(sb: sb,
                line:
                $"  call void @{mangledCrash}(ptr sret({textLlvm}) {sretPtr}, {llvmReceiverType} {errorVal})");
            EmitLine(sb: sb, line: $"  {textVal} = load {textLlvm}, ptr {sretPtr}");
        }
        else
        {
            EmitLine(sb: sb,
                line: $"  {textVal} = call {textLlvm} @{mangledCrash}({llvmReceiverType} {errorVal})");
        }
        dataPtr = NextTemp();
        EmitLine(sb: sb,
            line: $"  {dataPtr} = extractvalue {textLlvm} {textVal}, {dataIdx}");
        msgLen = NextTemp();
        EmitLine(sb: sb,
            line: $"  {msgLen} = extractvalue {textLlvm} {textVal}, {countIdx}");
    }

    private void EmitAbsent(StringBuilder sb, AbsentStatement absentStmt)
    {
        if (_currentRoutineIsFailable)
        {
            // The original failable routine (not a try_/check_/lookup_ variant) treats `absent`
            // as a runtime crash with `AbsentValueError`. Use the same rf_crash shape as
            // EmitThrow so the error message + location aren't blank in the trace.
            // type name + filename go through @rf_crash as cstr (i64 = byte-data pointer + length).
            // The message goes as a UTF-32 codepoint buffer (Text data layout), so we load
            // field 0 (codepoint ptr) and field 1 (codepoint count) from a Text-formatted
            // string constant — same shape EmitThrow extracts from crash_message().
            const string typeName = "AbsentValueError";
            string message =
                $"Routine '{_currentEmittingRoutine?.BaseName ?? "<unknown>"}' signaled absent.";
            string typeCStr = EmitCStringConstant(value: typeName);
            string fileCStr = EmitCStringConstant(value: absentStmt.Location.FileName);
            string msgTextPtr = EmitStringLiteralGlobal(value: message);

            string typeNameAsInt = NextTemp();
            EmitLine(sb: sb, line: $"  {typeNameAsInt} = ptrtoint ptr {typeCStr} to i64");
            string fileAsInt = NextTemp();
            EmitLine(sb: sb, line: $"  {fileAsInt} = ptrtoint ptr {fileCStr} to i64");
            // Extract codepoint buffer + count from the Text-shaped global.
            string msgDataPtr = NextTemp();
            EmitLine(sb: sb, line: $"  {msgDataPtr} = load ptr, ptr {msgTextPtr}");
            string msgCountField = NextTemp();
            EmitLine(sb: sb,
                line:
                $"  {msgCountField} = getelementptr {{ptr, i64}}, ptr {msgTextPtr}, i32 0, i32 1");
            string msgCount = NextTemp();
            EmitLine(sb: sb, line: $"  {msgCount} = load i64, ptr {msgCountField}");
            string msgAsInt = NextTemp();
            EmitLine(sb: sb, line: $"  {msgAsInt} = ptrtoint ptr {msgDataPtr} to i64");

            EmitRcRecordCleanup(sb: sb);
            EmitLine(sb: sb,
                line:
                $"  call void @rf_crash(i64 {typeNameAsInt}, i64 {typeName.Length}, i64 {fileAsInt}, i64 {absentStmt.Location.FileName.Length}, i32 {absentStmt.Location.Line}, i32 {absentStmt.Location.Column}, i64 {msgAsInt}, i64 {msgCount})");
            EmitLine(sb: sb, line: "  unreachable");
            return;
        }

        TypeInfo absentRetType = _currentEmittingRoutine!.ReturnType!;
        string absentCarrierType = GetLlvmType(type: absentRetType);
        EmitRcRecordCleanup(sb: sb);
        // Balance the routine-entry trace_push. Missing this leaks a frame on the shadow stack
        // every time a `try_X` variant returns absent (which happens at every for-loop exit).
        // Subsequent `_rf_trace_update_loc` calls in the caller then update the leaked frame's
        // slot instead of the caller's, corrupting the stack trace.
        if (_traceCurrentRoutine)
            EmitLine(sb: sb, line: TracePop);
        EmitLine(sb: sb, line: $"  ret {absentCarrierType} zeroinitializer");
    }

    #endregion
}
