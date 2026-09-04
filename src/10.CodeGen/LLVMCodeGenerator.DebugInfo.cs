using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using SyntaxTree;

namespace Compiler.CodeGen;

/// <summary>
/// Line-tables-only DWARF debug info emission, so tools (Compiler Explorer, debuggers) can map
/// source &lt;-&gt; LLVM IR. Gated on build mode via <see cref="ShouldEmitDebugInfo"/> (same modes as
/// the runtime trace, whose <c>@_rf_trace_push</c>/<c>@_rf_trace_update_loc</c> calls this pass reuses
/// as the per-instruction source-location cursor). Implemented as a post-process over the finished IR
/// text — mirroring <c>ApplyTbaa</c> — so the hot emission path stays untouched.
/// </summary>
public partial class LlvmCodeGenerator
{
    /// <summary>Emit debug info exactly when the runtime trace is emitted (Debug/Release builds); the
    /// trace calls it depends on for line/col are present under the same condition.</summary>
    private bool ShouldEmitDebugInfo => ShouldEmitTrace;

    /// <summary>Per-function DISubprogram descriptors, keyed by the (unquoted) LLVM function symbol.
    /// Captured at define-emission time where the routine's <see cref="SourceLocation"/> — including the
    /// real source file — is available; the post-process can only recover line/col from the trace calls,
    /// not the filename (a cstring global by then).</summary>
    private readonly Dictionary<string, DebugSubprogram> _debugSubprograms = new(comparer: StringComparer.Ordinal);

    /// <summary>A recorded DISubprogram: source file/dir (forward-slashed) plus the routine's start line.</summary>
    private readonly record struct DebugSubprogram(string File, string Directory, int Line);

    /// <summary>Layer-2 per-expression location cursor. Push/PopDebugLoc emit <c>; !diloc L C</c>
    /// markers into the instruction stream as codegen descends/ascends the AST; ApplyDebugInfo reads
    /// them (finer than the call-site trace cursor) to give each instruction its own line+column, then
    /// strips them from the output.</summary>
    private SourceLocation? _currentDbgLoc;

    /// <summary>Emits a location marker for <paramref name="loc"/> if it differs from the current
    /// cursor, and returns the previous cursor so the caller can restore it (see PopDebugLoc). No-op
    /// unless debug mode.</summary>
    private SourceLocation? PushDebugLoc(StringBuilder sb, SourceLocation? loc)
    {
        SourceLocation? prev = _currentDbgLoc;
        if (ShouldEmitDebugInfo && loc is not null &&
            (loc.Line != prev?.Line || loc.Column != prev?.Column))
        {
            sb.Append(value: "  ; !diloc ").Append(value: loc.Line).Append(value: ' ')
              .Append(value: loc.Column).Append(value: '\n');
            _currentDbgLoc = loc;
        }

        return prev;
    }

    /// <summary>Restores the cursor saved by PushDebugLoc, re-emitting its marker so instructions the
    /// parent node emits *after* a child get the parent's location. No-op unless debug mode.</summary>
    private void PopDebugLoc(StringBuilder sb, SourceLocation? prev)
    {
        if (ShouldEmitDebugInfo && prev is not null &&
            (prev.Line != _currentDbgLoc?.Line || prev.Column != _currentDbgLoc?.Column))
        {
            sb.Append(value: "  ; !diloc ").Append(value: prev.Line).Append(value: ' ')
              .Append(value: prev.Column).Append(value: '\n');
        }

        _currentDbgLoc = prev;
    }

    /// <summary>Records a DISubprogram descriptor for an RF routine define. No-op unless debug mode.</summary>
    private void RecordDebugSubprogram(string funcName, SourceLocation? location)
    {
        if (!ShouldEmitDebugInfo || location is null)
            return;
        string full = (location.FileName ?? "").Replace(oldChar: '\\', newChar: '/');
        string dir = full.Contains(value: '/') ? full[..full.LastIndexOf(value: '/')] : "";
        string file = full.Contains(value: '/') ? full[(full.LastIndexOf(value: '/') + 1)..] : full;
        if (string.IsNullOrEmpty(value: file))
            file = "unknown.rf";
        _debugSubprograms[key: StripQuotes(sym: funcName)] =
            new DebugSubprogram(File: file, Directory: dir, Line: location.Line);
    }

    /// <summary>
    /// Attaches line-tables-only debug metadata to the finished IR: <c>!dbg</c> on each RF-routine
    /// <c>define</c> (→ a distinct DISubprogram) and on each single-line instruction inside it (→ a
    /// DILocation for the current source line/col, tracked from the trace calls). Appends the module's
    /// <c>!llvm.dbg.cu</c> / <c>!llvm.module.flags</c> and the metadata block. No-op unless debug mode.
    /// </summary>
    private string ApplyDebugInfo(string ir)
    {
        if (!ShouldEmitDebugInfo || _debugSubprograms.Count == 0)
            return ir;

        // Metadata ids must not collide with any already in the IR (TBAA hardcodes !0..!22).
        int nextId = MaxMetadataId(ir: ir) + 1;

        var meta = new StringBuilder();
        var fileIds = new Dictionary<string, int>(comparer: StringComparer.Ordinal);
        var subIds = new Dictionary<string, int>(comparer: StringComparer.Ordinal);
        var locIds = new Dictionary<(int, int, int), int>();

        int subroutineTypeId = nextId++;
        meta.Append(value: $"!{subroutineTypeId} = !DISubroutineType(types: !{{null}})\n");

        int GetFile(string file, string dir)
        {
            string key = dir + "|" + file;
            if (fileIds.TryGetValue(key: key, out int id))
                return id;
            id = nextId++;
            fileIds[key: key] = id;
            meta.Append(
                value: $"!{id} = !DIFile(filename: \"{EscapeDi(s: file)}\", directory: \"{EscapeDi(s: dir)}\")\n");
            return id;
        }

        DebugSubprogram firstSp = _debugSubprograms.Values.First();
        int cuFileId = GetFile(file: firstSp.File, dir: firstSp.Directory);
        int cuId = nextId++;

        int GetSub(string funcName)
        {
            if (subIds.TryGetValue(key: funcName, out int id))
                return id;
            DebugSubprogram d = _debugSubprograms[key: funcName];
            int fileId = GetFile(file: d.File, dir: d.Directory);
            id = nextId++;
            subIds[key: funcName] = id;
            meta.Append(value:
                $"!{id} = distinct !DISubprogram(name: \"{EscapeDi(s: funcName)}\", scope: !{fileId}, " +
                $"file: !{fileId}, line: {d.Line}, type: !{subroutineTypeId}, scopeLine: {d.Line}, " +
                $"spFlags: DISPFlagDefinition, unit: !{cuId})\n");
            return id;
        }

        int GetLoc(int scope, int line, int col)
        {
            (int, int, int) key = (scope, line, col);
            if (locIds.TryGetValue(key: key, out int id))
                return id;
            id = nextId++;
            locIds[key: key] = id;
            meta.Append(value: $"!{id} = !DILocation(line: {line}, column: {col}, scope: !{scope})\n");
            return id;
        }

        var outSb = new StringBuilder(capacity: ir.Length + 8192);
        int curSub = -1, curLine = 0, curCol = 1;

        foreach (string line in ir.Split(separator: '\n'))
        {
            // Layer-2 fine-grained location markers: update the cursor and strip them from output.
            if (TryParseLocMarker(line: line, line2: out int ml, col: out int mc))
            {
                if (curSub >= 0)
                {
                    curLine = ml;
                    curCol = mc;
                }

                continue;
            }

            if (line.StartsWith(value: "define ", comparisonType: StringComparison.Ordinal))
            {
                string? sym = ExtractDefineSymbol(line: line);
                if (sym != null && _debugSubprograms.TryGetValue(key: sym, out DebugSubprogram sp))
                {
                    curSub = GetSub(funcName: sym);
                    curLine = sp.Line;
                    curCol = 1;
                    outSb.Append(value: AttachDbgToDefine(line: line, sub: curSub)).Append(value: '\n');
                }
                else
                {
                    curSub = -1;
                    outSb.Append(value: line).Append(value: '\n');
                }

                continue;
            }

            if (curSub >= 0 && line.StartsWith(value: "}", comparisonType: StringComparison.Ordinal))
            {
                curSub = -1;
                outSb.Append(value: line).Append(value: '\n');
                continue;
            }

            if (curSub >= 0)
            {
                if (TryParseTraceLoc(line: line, line2: out int l, col: out int c))
                {
                    curLine = l;
                    curCol = c;
                }

                if (IsInstructionLine(line: line))
                {
                    int loc = GetLoc(scope: curSub, line: curLine, col: curCol);
                    outSb.Append(value: line).Append(value: $", !dbg !{loc}").Append(value: '\n');
                    continue;
                }
            }

            outSb.Append(value: line).Append(value: '\n');
        }

        int flagsId = nextId++;
        AppendDebugMetadataTrailer(outSb: outSb, meta: meta, cuId: cuId, cuFileId: cuFileId,
            flagsId: flagsId);
        return outSb.ToString();
    }

    /// <summary>
    /// Appends the module's debug-info trailer to <paramref name="outSb"/>: the
    /// <c>!llvm.dbg.cu</c> / <c>!llvm.module.flags</c> named metadata, the DICompileUnit and Debug
    /// Info Version flag, and the accumulated per-descriptor <paramref name="meta"/> block.
    /// </summary>
    private static void AppendDebugMetadataTrailer(StringBuilder outSb, StringBuilder meta,
        int cuId, int cuFileId, int flagsId)
    {
        outSb.Append(value: $"\n!llvm.dbg.cu = !{{!{cuId}}}\n");
        outSb.Append(value: $"!llvm.module.flags = !{{!{flagsId}}}\n");
        outSb.Append(value:
            $"!{cuId} = distinct !DICompileUnit(language: DW_LANG_C99, file: !{cuFileId}, " +
            "producer: \"RazorForge\", isOptimized: false, runtimeVersion: 0, emissionKind: LineTablesOnly)\n");
        outSb.Append(value: $"!{flagsId} = !{{i32 2, !\"Debug Info Version\", i32 3}}\n");
        outSb.Append(value: meta);
    }

    // ---- helpers -------------------------------------------------------------------------------

    /// <summary>Largest existing <c>!N</c> metadata id in the IR (defs or refs); -1 if none.</summary>
    private static int MaxMetadataId(string ir)
    {
        int max = -1;
        foreach (Match m in Regex.Matches(input: ir, pattern: @"!(\d+)\b"))
            if (int.TryParse(s: m.Groups[1].Value, result: out int v) && v > max)
                max = v;
        return max;
    }

    /// <summary>Pulls the (unquoted) function symbol out of a <c>define ... @sym(...)</c> header.</summary>
    private static string? ExtractDefineSymbol(string line)
    {
        int at = line.IndexOf(value: '@');
        if (at < 0 || at + 1 >= line.Length)
            return null;
        int i = at + 1;
        if (line[i] == '"')
        {
            int close = line.IndexOf(value: '"', startIndex: i + 1);
            return close < 0 ? null : line[(i + 1)..close];
        }

        int paren = line.IndexOf(value: '(', startIndex: i);
        return paren < 0 ? null : line[i..paren];
    }

    /// <summary>Inserts <c>!dbg !sub</c> before the trailing <c>{</c> of a define header.</summary>
    private static string AttachDbgToDefine(string line, int sub)
    {
        int brace = line.LastIndexOf(value: '{');
        return brace < 0
            ? line
            : $"{line[..brace].TrimEnd()} !dbg !{sub} {line[brace..]}";
    }

    /// <summary>Parses a <c>  ; !diloc L C</c> Layer-2 location marker.</summary>
    private static bool TryParseLocMarker(string line, out int line2, out int col)
    {
        line2 = 0;
        col = 0;
        ReadOnlySpan<char> t = line.AsSpan().TrimStart();
        if (!t.StartsWith(value: "; !diloc "))
            return false;
        string[] parts = t[("; !diloc ".Length)..].ToString()
            .Split(separator: ' ', options: StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 &&
               int.TryParse(s: parts[0], result: out line2) &&
               int.TryParse(s: parts[1], result: out col);
    }

    private static readonly Regex TraceLocPattern =
        new(pattern: @"i32 (-?\d+), i32 (-?\d+)\)", options: RegexOptions.Compiled);

    /// <summary>Reads the current source (line, col) from an <c>@_rf_trace_push</c>/<c>update_loc</c> call.</summary>
    private static bool TryParseTraceLoc(string line, out int line2, out int col)
    {
        line2 = 0;
        col = 0;
        if (!line.Contains(value: "@_rf_trace_"))
            return false;
        Match m = TraceLocPattern.Match(input: line);
        if (!m.Success)
            return false;
        line2 = int.Parse(s: m.Groups[1].Value);
        col = int.Parse(s: m.Groups[2].Value);
        return true;
    }

    /// <summary>
    /// True for single-line instructions that legally accept a trailing <c>!dbg</c>. Conservative: skips
    /// multi-line <c>switch</c> (its <c>[</c>-opener, <c>iN …, label</c> case rows, and <c>]</c> closer),
    /// labels, braces, comments, and anything already carrying <c>!dbg</c>. A skipped instruction merely
    /// lacks a location (valid IR) — the invariant is: never produce malformed IR.
    /// </summary>
    private static bool IsInstructionLine(string line)
    {
        if (line.Length == 0 || (line[0] != ' ' && line[0] != '\t'))
            return false; // labels / define / module-level all sit at column 0
        string t = line.TrimStart();
        if (t.Length == 0 || t[0] is ';' or '{' or '}' or '[' or ']')
            return false;
        if (t.EndsWith(value: '[') || t.Contains(value: "!dbg"))
            return false; // '[' opens a multi-line switch
        if (t[0] == '%')
            return t.Contains(value: " = "); // "%x = <op> ..."
        foreach (string kw in InstructionOpcodes)
            if (t.StartsWith(value: kw, comparisonType: StringComparison.Ordinal))
                return true;
        return false; // notably excludes "switch " (multi-line) and switch case rows ("i64 …")
    }

    private static readonly string[] InstructionOpcodes =
    [
        "store ", "call ", "tail call ", "musttail ", "br ", "ret", "unreachable",
        "fence", "resume ", "cleanupret", "catchret", "indirectbr"
    ];

    private static string StripQuotes(string sym) =>
        sym.Length >= 2 && sym[0] == '"' && sym[^1] == '"' ? sym[1..^1] : sym;

    private static string EscapeDi(string s) =>
        s.Replace(oldValue: "\\", newValue: "/").Replace(oldValue: "\"", newValue: "\\22");
}
