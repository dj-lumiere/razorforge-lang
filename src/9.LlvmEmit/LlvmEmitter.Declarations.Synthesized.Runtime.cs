using System.Text;
using SyntaxTree;
using TypeModel.Symbols;

namespace Compiler.LlvmEmit;

/// <summary>
/// Declaration code generation for synthesized runtime-support routines.
/// </summary>
public partial class LlvmEmitter
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

        string returnType = routine.ReturnType != null
            ? GetLlvmType(type: routine.ReturnType)
            : "void";

        // Mirror GenerateRoutineDefinition's ABI return handling (sret for Indirect, integer coercion
        // for small structs): this synthesized define path must agree with the declaration
        // GenerateRoutineDeclaration emitted, or the declare/define signature-match invariant trips.
        bool prevReturnViaSret = _currentReturnViaSret;
        string? prevReturnCoerce = _currentReturnCoerceType;
        _currentReturnViaSret = ReturnsViaSret(routine: routine);
        _currentReturnCoerceType = _currentReturnViaSret
            ? null
            : ReturnCoerceType(routine: routine);
        if (_currentReturnViaSret)
        {
            paramList.Insert(index: 0, item: $"ptr sret({returnType}) %sret");
        }

        string headerReturnType = _currentReturnViaSret
            ? "void"
            : _currentReturnCoerceType ?? returnType;
        string parameters = string.Join(separator: ", ", values: paramList);

        int savedLength = _functionDefinitions.Length;
        int savedTempCounter = _tempCounter;
        // Same whole-program-internal treatment as GenerateRoutineDefinition — routed through the shared
        // ComputeRoutineLinkage so both header emitters agree cold-vs-warm (see its doc for the rationale).
        (bool isCompilerGenerated, string linkagePrefix) = ComputeRoutineLinkage(routine: routine);
        string synthAttrs = isCompilerGenerated
            ? " nounwind"
            : "";
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
            string meType = GetImplicitMeParameterDeclaration(routine: routine, includeName: true);
            if (!meType.StartsWith(value: "void", comparisonType: StringComparison.Ordinal))
            {
                paramList.Add(item: meType);
            }
        }

        paramList.AddRange(collection:
            from param in routine.Parameters
            let byval = ParameterPassedByval(routine: routine, paramType: param.Type)
            let coerce = byval
                ? null
                : ParameterCoerceType(routine: routine, paramType: param.Type)
            let paramType = byval
                ? $"ptr byval({GetLlvmType(type: param.Type)})"
                : coerce ?? GetParameterLlvmType(type: param.Type)
            let emittedName = GetEmittedParamName(byval: byval, name: param.Name)
            select $"{paramType} %{emittedName}");
        return paramList;
    }

    /// <summary>Returns the LLVM parameter name for a routine parameter. Byval parameters use a
    /// <c>.addr</c> suffix; the reserved name <c>entry</c> is escaped to <c>entry_</c> to avoid
    /// colliding with the LLVM basic-block label of the same name; all other names are used as-is.</summary>
    private static string GetEmittedParamName(bool byval, string name)
    {
        if (byval)
        {
            return $"{name}.addr";
        }

        if (name == "entry")
        {
            return "entry_";
        }

        return name;
    }
}
