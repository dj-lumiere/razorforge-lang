using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Compiler.CodeGen;

/// <summary>
/// Declaration code generation for synthesized runtime-support routines.
/// </summary>
public partial class LlvmCodeGenerator
{
    private void EmitSynthesizedBodyFromAst(RoutineInfo routine, string funcName, Statement body)
    {
        // Base mode (resident-JIT incremental, §2A.5): a synthesized body whose signature still carries an
        // unresolved generic parameter — e.g. List[Character].from_literal(elements: Array[Character,
        // __Vararg0]), a const-generic arity template that reaches here via Phase B's IsSynthesized branch —
        // would emit malformed IR. Such templates instantiate on demand, never in the non-pruned base. This
        // is the bypass path that skips GenerateRoutineDefinition's ShouldSkipRoutineDefinition gate.
        if (_baseMode && SignatureHasUnresolvedGeneric(r: routine))
        {
            return;
        }

        List<string> paramList = BuildSynthesizedParameterList(routine: routine);

        string returnType = routine.ReturnType != null ? GetLlvmType(type: routine.ReturnType) : "void";

        // Mirror GenerateRoutineDefinition's ABI return handling (sret for Indirect, integer coercion
        // for small structs): this synthesized define path must agree with the declaration
        // GenerateRoutineDeclaration emitted, or the declare/define signature-match invariant trips.
        bool prevReturnViaSret = _currentReturnViaSret;
        string? prevReturnCoerce = _currentReturnCoerceType;
        _currentReturnViaSret = ReturnsViaSret(routine: routine);
        _currentReturnCoerceType = _currentReturnViaSret ? null : ReturnCoerceType(routine: routine);
        if (_currentReturnViaSret)
        {
            paramList.Insert(index: 0, item: $"ptr sret({returnType}) %sret");
        }

        string headerReturnType = _currentReturnViaSret ? "void"
            : _currentReturnCoerceType ?? returnType;
        string parameters = string.Join(separator: ", ", values: paramList);

        int savedLength = _functionDefinitions.Length;
        int savedTempCounter = _tempCounter;
        // Same whole-program-internal treatment as GenerateRoutineDefinition: these are all
        // compiler-synthesized bodies (auto-derived destroy/store/copy, wrapper forwarding, …),
        // referenced only within this module, so `internal` linkage lets GlobalDCE strip the uncalled
        // ones and `nounwind` reflects that the runtime never unwinds.
        // Base mode: EXTERNAL so the delta module can reference it (internal is module-local, invisible
        // across the base/delta split). Base is non-pruned + cached ⇒ no GlobalDCE needed.
        // Deterministic across cold/warm — see BuildDefineHeader: a monomorphized instance (owner carries
        // concrete type arguments) is whole-program-internal BY STRUCTURE, independent of the IsSynthesized
        // flag, which drifts cold-vs-warm on a demand-built routine. Both header emitters must agree.
        bool ownerIsMonomorphizedInstance =
            routine.OwnerType is { IsGenericDefinition: false, TypeArguments.Count: > 0 };
        bool isCompilerGenerated =
            routine.IsSynthesized || routine.IsWiredMemberRoutine || ownerIsMonomorphizedInstance;
        string linkagePrefix = isCompilerGenerated && !_baseMode ? "internal " : "";
        string synthAttrs = isCompilerGenerated ? " nounwind" : "";
        string defineHeader =
            $"define {linkagePrefix}{headerReturnType} @{funcName}({parameters}){synthAttrs} {{";
        _generatedRoutineDefHeaders[key: funcName] = defineHeader;
        EmitLine(sb: _functionDefinitions, line: defineHeader);
        EmitLine(sb: _functionDefinitions, line: "entry:");
        var bodyBuilder = new StringBuilder();
        try
        {
            GenerateRoutineBody(sb: bodyBuilder, body: body, routine: routine);
            _functionDefinitions.Append(value: _currentRoutineEntryAllocas);
            _functionDefinitions.Append(value: bodyBuilder);
        }
        catch
        {
            _functionDefinitions.Length = savedLength;
            _tempCounter = savedTempCounter;
            _generatedRoutineDefs.Remove(item: funcName);
            _generatedRoutineDefHeaders.Remove(key: funcName);
            throw;
        }
        EmitLine(sb: _functionDefinitions, line: "}");
        EmitLine(sb: _functionDefinitions, line: "");
        _currentReturnViaSret = prevReturnViaSret;
        _currentReturnCoerceType = prevReturnCoerce;
    }

    /// <summary>
    /// Builds the LLVM parameter list (with names) for a synthesized routine body: the implicit
    /// <c>me</c> receiver for memberRoutines (skipping create factories, common routines, and void
    /// <c>me</c>), then each explicit parameter in its ABI passing form (byval / coerce / plain value).
    /// </summary>
    private List<string> BuildSynthesizedParameterList(RoutineInfo routine)
    {
        var paramList = new List<string>();
        if (routine.OwnerType != null && !IsCreatorRoutine(routine: routine) && !routine.IsCommon)
        {
            string meType =
                GetImplicitMeParameterDeclaration(routine: routine, includeName: true);
            if (!meType.StartsWith(value: "void", comparisonType: StringComparison.Ordinal))
                paramList.Add(item: meType);
        }
        paramList.AddRange(collection:
            from param in routine.Parameters
            let byval = ParameterPassedByval(routine: routine, paramType: param.Type)
            let coerce = byval ? null : ParameterCoerceType(routine: routine, paramType: param.Type)
            let paramType = byval ? $"ptr byval({GetLlvmType(type: param.Type)})"
                : coerce ?? GetParameterLlvmType(type: param.Type)
            let emittedName = byval ? $"{param.Name}.addr"
                : param.Name == "entry" ? "entry_" : param.Name
            select $"{paramType} %{emittedName}");
        return paramList;
    }

}
