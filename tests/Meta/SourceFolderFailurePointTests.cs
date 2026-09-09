using System.Text.RegularExpressions;

namespace RazorForge.Tests.Meta;

/// <summary>
/// Verifies that each compiler source folder retains explicit handling for its known failure points.
/// </summary>
public sealed partial class SourceFolderFailurePointTests
{
    /// <summary>
    /// Provides source-folder failure point expectations.
    /// </summary>
    public static TheoryData<string, string, string[]> FailurePointExpectations => new()
    {
        {
            "BuildSystem",
            "manifest parsing validates required fields, indexes modules, and the native toolchain detects linker failures",
            [
                "ReadRequiredString", "BuildModuleIndex", "ExtractModuleName",
                "DetectLinkerFromStderr"
            ]
        },
        // Phase folders now numbered 1..8 by pull/(B) pipeline order: 1 tokenize · 2 parse · 3 declarations ·
        // 4 desugar · 5 collect-from-start · 6 semantic errors · 7 monomorphize · 8 LLVM IR.
        {
            "1.Tokenizer",
            "source validation rejects ambiguous bytes and whitespace before scanning",
            [
                "NormalizeAndValidateSource", "Source contains a null byte",
                "Tabs are not allowed", "Unsupported whitespace character"
            ]
        },
        {
            "2.Parser",
            "parse errors are recorded and synchronized instead of aborting whole files",
            ["HasErrors", "Synchronize", "ExpectedIndentedBlock", "ProcessDedentTokens"]
        },
        {
            "3.Declaration",
            "declaration collection + name/type resolution handle import graphs, stdlib registration, and overload lookup",
            [
                "ModuleDependencyGraph", "RegisterProgramTypes", "SignatureResolver",
                "LookupMemberRoutineOverload"
            ]
        },
        {
            "4.Desugaring",
            "operator + syntax desugaring and type-aware lowering cover user + variant bodies",
            [
                "DesugaringPipeline", "PostprocessingPipeline", "OperatorLoweringPass",
                "FStringLoweringPass", "ControlFlowLoweringPass"
            ]
        },
        {
            "5.Collection",
            "demand collection walks the closure reachable from start (+ retiring push reachability)",
            ["RoutineCollectionPass", "RoutineReachabilityPass", "ReachableGenericCollectionPass"]
        },
        {
            "6.Verification",
            "semantic analysis runs ordered phases and reports diagnostics instead of raw exceptions",
            [
                "RunPhase1Declarations", "RunPhase2Resolution", "RunPhase5SemanticAnalysis",
                "ReportError"
            ]
        },
        {
            "7.Instantiation",
            "instantiation monomorphizes + copies reachable bodies AND synthesizes wired/error-variant routines",
            [
                "GenericMonomorphizationPass", "MonomorphizedBody", "WiredRoutinePass",
                "ErrorHandlingVariantPass"
            ]
        },
        {
            "8.CodeGen",
            "backend rejects unsupported AST/metadata states before emitting invalid IR",
            [
                "InvalidOperationException", "NotImplementedException", "GetExpressionType",
                "GenerateRoutineDefinitions"
            ]
        },
        {
            "Execution", "CLI paths handle file, grammar, and native build failures",
            ["File.Exists", "catch (GrammarException", "BuildNativeRuntime"]
        },
        {
            "Targeting",
            "target detection rejects unsupported platforms and carries target layout metadata",
            ["PlatformNotSupportedException", "PointerBitWidth", "DataLayout"]
        },
        {
            "TypeModel",
            "type metadata protects invalid generic substitutions and placeholder use",
            ["Substitute", "GenericParameters", "TypeArguments", "InvalidOperationException"]
        },
        {
            "SyntaxTree", "all AST nodes preserve source locations for downstream diagnostics",
            ["SourceLocation", "ISyntaxTreeNode", "Location"]
        },
        {
            "Diagnostics",
            "diagnostics retain stable codes, source locations, and formatted messages",
            ["GrammarDiagnosticCode", "SemanticDiagnosticCode", "FormatMessage"]
        },
        {
            "Debug", "debug AST printing includes explicit visitors for generated tree shapes",
            ["RfSyntaxTreePrinter", "Visit", "Accept"]
        }
    };

    /// <summary>
    /// Verifies that the source folder has the expected failure-point implementation hooks.
    /// </summary>
    /// <param name="folder">The source folder name.</param>
    /// <param name="description">The failure point description.</param>
    /// <param name="requiredFragments">The source fragments that should be present.</param>
    [Theory]
    [MemberData(memberName: nameof(FailurePointExpectations))]
    public void SourceFolder_RetainsFailurePointHooks(string folder, string description,
        string[] requiredFragments)
    {
        string source = ReadSourceFolder(folder: folder);

        foreach (string fragment in requiredFragments)
        {
            Assert.Contains(expectedSubstring: fragment,
                actualString: source,
                comparisonType: StringComparison.Ordinal);
        }

        Assert.False(condition: string.IsNullOrWhiteSpace(value: description));
    }

    /// <summary>
    /// Verifies that risky backend exceptions are confined to backend implementation files.
    /// </summary>
    [Fact]
    public void FrontendFolders_DoNotThrowRawNotImplementedExceptions()
    {
        string sourceRoot = FindSourceRoot();
        string[] frontendFolders =
        [
            "BuildSystem",
            "1.Tokenizer",
            "2.Parser",
            "3.Declaration",
            "4.Desugaring",
            "5.Collection",
            "6.Verification"
        ];

        var offenders = frontendFolders.SelectMany(selector: folder =>
                                            Directory.EnumerateFiles(
                                                path: Path.Combine(path1: sourceRoot,
                                                    path2: folder),
                                                searchPattern: "*.cs",
                                                searchOption: SearchOption.AllDirectories))
                                       .Where(predicate: path => File.ReadAllText(path: path)
                                           .Contains(value: "throw new NotImplementedException",
                                                comparisonType: StringComparison.Ordinal))
                                       .Select(selector: path => Path.GetRelativePath(
                                            relativeTo: sourceRoot,
                                            path: path))
                                       .ToList();

        Assert.Empty(collection: offenders);
    }

    /// <summary>
    /// Verifies that every source folder has at least one test file naming the folder or a primary type.
    /// </summary>
    /// <param name="folder">The source folder name.</param>
    /// <param name="description">The failure point description.</param>
    /// <param name="requiredFragments">Unused source fragments for member data compatibility.</param>
    [Theory]
    [MemberData(memberName: nameof(FailurePointExpectations))]
    public void SourceFolder_HasAssociatedTestSurface(string folder, string description,
        string[] requiredFragments)
    {
        string testRoot = FindTestRoot();
        string testText = string.Join(separator: "\n",
            values: Directory.EnumerateFiles(path: testRoot,
                                  searchPattern: "*.cs",
                                  searchOption: SearchOption.AllDirectories)
                             .Where(predicate: path => !path.EndsWith(
                                  value: $"{Path.DirectorySeparatorChar}GlobalUsings.cs",
                                  comparisonType: StringComparison.Ordinal))
                             .Select(selector: File.ReadAllText));

        string source = ReadSourceFolder(folder: folder);
        string[] anchors = PrimaryPublicTypePattern()
                          .Matches(input: source)
                          .Select(selector: match => match.Groups[groupname: "name"].Value)
                          .Append(element: folder)
                          .Distinct(comparer: StringComparer.Ordinal)
                          .ToArray();

        Assert.Contains(collection: anchors,
            filter: anchor => testText.Contains(value: anchor,
                comparisonType: StringComparison.Ordinal));
        Assert.NotEmpty(collection: requiredFragments);
        Assert.False(condition: string.IsNullOrWhiteSpace(value: description));
    }

    private static string ReadSourceFolder(string folder)
    {
        string folderPath = Path.Combine(path1: FindSourceRoot(), path2: folder);
        Assert.True(condition: Directory.Exists(path: folderPath),
            userMessage: $"Missing source folder '{folder}'.");

        string[] files = Directory.EnumerateFiles(path: folderPath,
                                       searchPattern: "*.cs",
                                       searchOption: SearchOption.AllDirectories)
                                  .Where(predicate: path => !path.EndsWith(value: ".cs~",
                                       comparisonType: StringComparison.Ordinal))
                                  .OrderBy(keySelector: path => path,
                                       comparer: StringComparer.Ordinal)
                                  .ToArray();
        Assert.NotEmpty(collection: files);

        return string.Join(separator: "\n", values: files.Select(selector: File.ReadAllText));
    }

    private static string FindSourceRoot()
    {
        string current = AppContext.BaseDirectory;

        while (!string.IsNullOrEmpty(value: current))
        {
            string candidate = Path.Combine(path1: current, path2: "src");
            if (Directory.Exists(path: candidate) &&
                Directory.Exists(path: Path.Combine(path1: candidate, path2: "1.Tokenizer")))
            {
                return candidate;
            }

            DirectoryInfo? parent = Directory.GetParent(path: current);
            current = parent?.FullName ?? string.Empty;
        }

        throw new DirectoryNotFoundException(message: "Could not find the src directory.");
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

    [GeneratedRegex(pattern: @"\b(?:class|record|enum)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)",
        options: RegexOptions.Compiled)]
    private static partial Regex PrimaryPublicTypePattern();
}
