using System.Text;

namespace Compilers.Shared.CodeGen;

/// <summary>
/// Generates LLVM IR code for runtime stack trace support.
/// Handles stack frame push/pop at routine entry/exit, and stack capture on throw/absent.
/// </summary>
public class StackTraceCodeGen
{
    private readonly SymbolTables _symbolTables;
    private readonly StringBuilder _output;

    /// <summary>
    /// Whether stack trace generation is enabled.
    /// Can be disabled for release builds or freestanding mode without stack trace support.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Maximum stack depth for stack trace capture.
    /// </summary>
    public int MaxStackDepth { get; set; } = 256;

    public StackTraceCodeGen(SymbolTables symbolTables, StringBuilder output)
    {
        _symbolTables = symbolTables;
        _output = output;
    }

    /// <summary>
    /// Emits global declarations for stack trace runtime support.
    /// Call this during module initialization.
    /// </summary>
    public void EmitGlobalDeclarations()
    {
        if (!Enabled) return;

        _output.AppendLine(value: "; Stack trace runtime support");
        _output.AppendLine(value: "; __rf_stack_push(file_id, routine_id, type_id, line, column)");
        _output.AppendLine(value: "declare void @__rf_stack_push(i32, i32, i32, i32, i32)");
        _output.AppendLine(value: "declare void @__rf_stack_pop()");
        _output.AppendLine(value: "declare void @__rf_stack_capture()");
        _output.AppendLine(value: "declare void @__rf_throw(i8*, i8*)");
        _output.AppendLine(value: "declare void @__rf_throw_absent()");
        _output.AppendLine();
    }

    /// <summary>
    /// Emits the file and routine name tables as global constants.
    /// Call this after all files and routines have been registered.
    /// </summary>
    public void EmitSymbolTables()
    {
        if (!Enabled) return;

        _output.AppendLine(value: "; File name table");
        for (int i = 0; i < _symbolTables.FileCount; i++)
        {
            string file = _symbolTables.GetFileName(fileId: (uint)i);
            string escaped = EscapeLLVMString(s: file);
            int len = file.Length + 1; // +1 for null terminator
            _output.AppendLine(
                value: $"@__rf_file_{i} = private unnamed_addr constant [{len} x i8] c\"{escaped}\\00\", align 1");
        }
        _output.AppendLine();

        _output.AppendLine(value: "; Routine name table");
        for (int i = 0; i < _symbolTables.RoutineCount; i++)
        {
            string routine = _symbolTables.GetRoutineName(routineId: (uint)i);
            string escaped = EscapeLLVMString(s: routine);
            int len = routine.Length + 1; // +1 for null terminator
            _output.AppendLine(
                value: $"@__rf_routine_{i} = private unnamed_addr constant [{len} x i8] c\"{escaped}\\00\", align 1");
        }
        _output.AppendLine();

        // Emit table arrays for runtime lookup
        _output.AppendLine(value: "; File table array");
        if (_symbolTables.FileCount > 0)
        {
            var fileRefs = new List<string>();
            for (int i = 0; i < _symbolTables.FileCount; i++)
            {
                string file = _symbolTables.GetFileName(fileId: (uint)i);
                int len = file.Length + 1;
                fileRefs.Add(item: $"i8* getelementptr inbounds ([{len} x i8], [{len} x i8]* @__rf_file_{i}, i32 0, i32 0)");
            }
            _output.AppendLine(
                value: $"@__rf_file_table = private constant [{_symbolTables.FileCount} x i8*] [{string.Join(separator: ", ", values: fileRefs)}], align 8");
        }
        _output.AppendLine();

        _output.AppendLine(value: "; Routine table array");
        if (_symbolTables.RoutineCount > 0)
        {
            var routineRefs = new List<string>();
            for (int i = 0; i < _symbolTables.RoutineCount; i++)
            {
                string routine = _symbolTables.GetRoutineName(routineId: (uint)i);
                int len = routine.Length + 1;
                routineRefs.Add(item: $"i8* getelementptr inbounds ([{len} x i8], [{len} x i8]* @__rf_routine_{i}, i32 0, i32 0)");
            }
            _output.AppendLine(
                value: $"@__rf_routine_table = private constant [{_symbolTables.RoutineCount} x i8*] [{string.Join(separator: ", ", values: routineRefs)}], align 8");
        }
        _output.AppendLine();

        _output.AppendLine(value: "; Type name table");
        for (int i = 0; i < _symbolTables.TypeCount; i++)
        {
            string type = _symbolTables.GetTypeName(typeId: (uint)i);
            string escaped = EscapeLLVMString(s: type);
            int len = type.Length + 1;
            _output.AppendLine(
                value: $"@__rf_type_{i} = private unnamed_addr constant [{len} x i8] c\"{escaped}\\00\", align 1");
        }
        _output.AppendLine();

        _output.AppendLine(value: "; Type table array");
        if (_symbolTables.TypeCount > 0)
        {
            var typeRefs = new List<string>();
            for (int i = 0; i < _symbolTables.TypeCount; i++)
            {
                string type = _symbolTables.GetTypeName(typeId: (uint)i);
                int len = type.Length + 1;
                typeRefs.Add(item: $"i8* getelementptr inbounds ([{len} x i8], [{len} x i8]* @__rf_type_{i}, i32 0, i32 0)");
            }
            _output.AppendLine(
                value: $"@__rf_type_table = private constant [{_symbolTables.TypeCount} x i8*] [{string.Join(separator: ", ", values: typeRefs)}], align 8");
        }
        _output.AppendLine();
    }

    /// <summary>
    /// Emits code to push a stack frame at routine entry.
    /// </summary>
    /// <param name="fileId">The file ID from SymbolTables</param>
    /// <param name="routineId">The routine ID from SymbolTables</param>
    /// <param name="typeId">The type ID (record/entity/resident) or 0 for free functions</param>
    /// <param name="line">The line number of the routine definition</param>
    /// <param name="column">The column number of the routine definition</param>
    public void EmitPushFrame(uint fileId, uint routineId, uint typeId, uint line, uint column)
    {
        if (!Enabled) return;

        _output.AppendLine(
            value: $"  call void @__rf_stack_push(i32 {fileId}, i32 {routineId}, i32 {typeId}, i32 {line}, i32 {column})");
    }

    /// <summary>
    /// Emits code to pop a stack frame at routine exit (before ret).
    /// </summary>
    public void EmitPopFrame()
    {
        if (!Enabled) return;

        _output.AppendLine(value: "  call void @__rf_stack_pop()");
    }

    /// <summary>
    /// Emits code for a throw statement.
    /// Captures the stack trace and throws the error.
    /// </summary>
    /// <param name="errorTypePtr">LLVM value pointing to error type name</param>
    /// <param name="messagePtr">LLVM value pointing to error message</param>
    public void EmitThrow(string errorTypePtr, string messagePtr)
    {
        if (!Enabled)
        {
            // Even without stack traces, we need to throw
            _output.AppendLine(value: $"  call void @__rf_throw(i8* {errorTypePtr}, i8* {messagePtr})");
            _output.AppendLine(value: "  unreachable");
            return;
        }

        _output.AppendLine(value: "  call void @__rf_stack_capture()");
        _output.AppendLine(value: $"  call void @__rf_throw(i8* {errorTypePtr}, i8* {messagePtr})");
        _output.AppendLine(value: "  unreachable");
    }

    /// <summary>
    /// Emits code for an absent statement.
    /// Captures the stack trace and throws AbsentValueError.
    /// </summary>
    public void EmitAbsent()
    {
        if (!Enabled)
        {
            _output.AppendLine(value: "  call void @__rf_throw_absent()");
            _output.AppendLine(value: "  unreachable");
            return;
        }

        _output.AppendLine(value: "  call void @__rf_stack_capture()");
        _output.AppendLine(value: "  call void @__rf_throw_absent()");
        _output.AppendLine(value: "  unreachable");
    }

    /// <summary>
    /// Escapes a string for use in LLVM IR string constants.
    /// </summary>
    private static string EscapeLLVMString(string s)
    {
        var sb = new StringBuilder();
        foreach (char c in s)
        {
            if (c == '\\')
                sb.Append(value: "\\5C");
            else if (c == '"')
                sb.Append(value: "\\22");
            else if (c < 32 || c > 126)
                sb.Append(value: $"\\{(int)c:X2}");
            else
                sb.Append(value: c);
        }
        return sb.ToString();
    }
}
