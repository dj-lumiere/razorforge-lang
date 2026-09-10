using SyntaxTree;

namespace Builder.Execution;

internal partial class Program
{
    private static void PrintDeclarationSummary(ISyntaxTreeNode node, int indent)
    {
        string prefix = new(c: ' ', count: indent * 2);

        switch (node)
        {
            case RoutineDeclaration func:
                string funcModifiers =
                    string.Join(separator: " ", values: GetModifiers(func: func));
                Console.WriteLine(
                    value:
                    $"{prefix}func {func.Name}({func.Parameters.Count} params) -> {func.ReturnType?.ToString() ?? "void"} {funcModifiers}"
                       .TrimEnd());
                break;

            case RecordDeclaration rec:
                string recModifiers = string.Join(separator: " ", values: GetModifiers(rec: rec));
                Console.WriteLine(
                    value:
                    $"{prefix}record {rec.Name} ({rec.Members.Count} members) {recModifiers}"
                       .TrimEnd());
                break;

            case EntityDeclaration ent:
                string entModifiers = string.Join(separator: " ", values: GetModifiers(ent: ent));
                Console.WriteLine(
                    value:
                    $"{prefix}entity {ent.Name} ({ent.Members.Count} members) {entModifiers}"
                       .TrimEnd());
                break;

            case ChoiceDeclaration choice:
                Console.WriteLine(
                    value: $"{prefix}choice {choice.Name} ({choice.Cases.Count} variants)");
                break;

            case VariantDeclaration variant:
                Console.WriteLine(
                    value: $"{prefix}variant {variant.Name} ({variant.Members.Count} members)");
                break;

            case ProtocolDeclaration proto:
                Console.WriteLine(
                    value:
                    $"{prefix}protocol {proto.Name} ({proto.MemberRoutines.Count} member routines)");
                break;

            case ImportDeclaration import:
                Console.WriteLine(value: $"{prefix}import {import.ModulePath}");
                break;

            case ModuleDeclaration ns:
                Console.WriteLine(value: $"{prefix}module {ns.Path}");
                break;

            default:
                Console.WriteLine(value: $"{prefix}{node.GetType().Name}");
                break;
        }
    }

    /// <summary>Returns a list of modifier tokens (e.g., generic parameter lists) for a routine declaration.</summary>
    private static List<string> GetModifiers(RoutineDeclaration func)
    {
        var mods = new List<string>();
        if (func.GenericParameters?.Count > 0)
        {
            mods.Add(item: $"[{string.Join(separator: ", ", values: func.GenericParameters)}]");
        }

        return mods;
    }

    /// <summary>Returns a list of modifier tokens (e.g., generic parameter lists) for a record declaration.</summary>
    private static List<string> GetModifiers(RecordDeclaration rec)
    {
        var mods = new List<string>();
        if (rec.GenericParameters?.Count > 0)
        {
            mods.Add(item: $"[{string.Join(separator: ", ", values: rec.GenericParameters)}]");
        }

        return mods;
    }

    /// <summary>Returns a list of modifier tokens (e.g., generic parameter lists) for an entity declaration.</summary>
    private static List<string> GetModifiers(EntityDeclaration ent)
    {
        var mods = new List<string>();
        if (ent.GenericParameters?.Count > 0)
        {
            mods.Add(item: $"[{string.Join(separator: ", ", values: ent.GenericParameters)}]");
        }

        return mods;
    }

    /// <summary>
    /// Escapes newline, carriage return, and tab characters in a string to their
    /// backslash-escaped equivalents for safe display on a single console line.
    /// </summary>
    private static string EscapeString(string s)
    {
        return s.Replace(oldValue: "\n", newValue: "\\n")
                .Replace(oldValue: "\r", newValue: "\\r")
                .Replace(oldValue: "\t", newValue: "\\t");
    }
}
