using System.Text.RegularExpressions;

namespace RazorForge.Tests.Meta;

/// <summary>
/// Tests that test memberRoutines contain an assertion path.
/// </summary>
public sealed partial class AssertionPresenceTests
{
    private static readonly Regex TestAttributePattern = MyRegex();

    private static readonly Regex memberRoutinePattern = MyRegex1();

    private static readonly string[] AssertionMarkers =
    [
        "Assert.",
        "Record.Exception",
        "AssertParses",
        "AssertParsesSuflae",
        "AssertParseError",
        "AssertAnalyzes",
        "AssertAnalyzesSuflae",
        "AssertHasError",
        "AssertHasErrorSuflae",
        "GenerateIr",
        "AssertBundleCompilesInProcess"
    ];

    /// <summary>
    /// Verifies that the test validates memberRoutines contain assertion path.
    /// </summary>
    [Fact]
    public void TestMemberRoutines_ContainAssertionPath()
    {
        string testRoot = FindTestRoot();
        var memberRoutinesWithoutAssertions = Directory.EnumerateFiles(path: testRoot,
                                                            searchPattern: "*.cs",
                                                            searchOption: SearchOption
                                                               .AllDirectories)
                                                       .Where(predicate: path => !path.EndsWith(
                                                            value:
                                                            $"{Path.DirectorySeparatorChar}GlobalUsings.cs",
                                                            comparisonType: StringComparison
                                                               .Ordinal))
                                                       .SelectMany(
                                                            selector:
                                                            FindTestMemberRoutinesWithoutAssertions)
                                                       .ToList();

        Assert.Empty(collection: memberRoutinesWithoutAssertions);
    }

    private static IEnumerable<string> FindTestMemberRoutinesWithoutAssertions(string filePath)
    {
        string[] lines = File.ReadAllLines(path: filePath);

        for (int i = 0; i < lines.Length; i++)
        {
            if (!TestAttributePattern.IsMatch(input: lines[i]))
            {
                continue;
            }

            int memberRoutineLine = FindNextMemberRoutineLine(lines: lines, startLine: i + 1);
            if (memberRoutineLine < 0)
            {
                continue;
            }

            string memberRoutineName = memberRoutinePattern.Match(input: lines[memberRoutineLine])
                                                           .Groups[groupname: "name"].Value;
            string memberRoutineBody =
                ExtractMemberRoutineBody(lines: lines, memberRoutineLine: memberRoutineLine);

            if (!AssertionMarkers.Any(predicate: marker => memberRoutineBody.Contains(
                    value: marker,
                    comparisonType: StringComparison.Ordinal)))
            {
                yield return
                    $"{Path.GetRelativePath(relativeTo: FindTestRoot(), path: filePath)}::{memberRoutineName}";
            }
        }
    }

    private static int FindNextMemberRoutineLine(string[] lines, int startLine)
    {
        for (int i = startLine; i < lines.Length; i++)
        {
            if (memberRoutinePattern.IsMatch(input: lines[i]))
            {
                return i;
            }

            if (TestAttributePattern.IsMatch(input: lines[i]))
            {
                return -1;
            }
        }

        return -1;
    }

    private static string ExtractMemberRoutineBody(string[] lines, int memberRoutineLine)
    {
        var bodyLines = new List<string>();
        int braceDepth = 0;
        bool foundOpeningBrace = false;

        for (int i = memberRoutineLine; i < lines.Length; i++)
        {
            string line = lines[i];
            bodyLines.Add(item: line);

            foreach (char ch in line)
            {
                if (ch == '{')
                {
                    braceDepth++;
                    foundOpeningBrace = true;
                }
                else if (ch == '}')
                {
                    braceDepth--;
                }
            }

            if (foundOpeningBrace && braceDepth == 0)
            {
                break;
            }
        }

        return string.Join(separator: Environment.NewLine, values: bodyLines);
    }

    private static string FindTestRoot()
    {
        string current = AppContext.BaseDirectory;

        while (!string.IsNullOrEmpty(value: current))
        {
            string candidate = Path.Combine(path1: current, path2: "tests");
            if (Directory.Exists(path: candidate) &&
                File.Exists(path: Path.Combine(path1: candidate, path2: "TestHelpers.cs")))
            {
                return candidate;
            }

            DirectoryInfo? parent = Directory.GetParent(path: current);
            current = parent?.FullName ?? string.Empty;
        }

        throw new DirectoryNotFoundException(message: "Could not find the tests directory.");
    }

    [GeneratedRegex(pattern: @"^\s*\[(?:Fact|Theory)\b", options: RegexOptions.Compiled)]
    private static partial Regex MyRegex();
    [GeneratedRegex(
        pattern: @"^\s*public\s+(?:async\s+)?(?:Task|void)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\(",
        options: RegexOptions.Compiled)]
    private static partial Regex MyRegex1();
}
