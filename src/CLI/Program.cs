using Compilers.Shared.Parser;
using Compilers.Shared.Lexer;
using Compilers.Shared.Analysis;
using Compilers.Shared.CodeGen;
using Compilers.RazorForge.Parser;
using Compilers.Suflae.Parser;
using Compilers.Shared.AST;

namespace Compilers;

internal class Program
{
    public static void Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return;
        }

        string command = args[0]
           .ToLowerInvariant();

        // Check if first arg is a command or a file
        bool isCommand = command == "compile" || command == "run" || command == "compileandrun" ||
                         command == "check" || command == "lsp";

        if (!isCommand)
        {
            // Old behavior: just a file path, compile it
            CompileFile(sourceFile: args[0], executeAfter: false,
                programArgs: Array.Empty<string>());
        }
        else if (command == "lsp")
        {
            Console.WriteLine(value: "Language Server not yet implemented.");
            Console.WriteLine(
                value: "This would start the RazorForge Language Server Protocol implementation.");
        }
        else
        {
            // New behavior: command + file
            if (args.Length < 2)
            {
                PrintUsage();
                return;
            }

            string sourceFile = args[1];
            string[] programArgs = args.Length > 2
                ? args[2..]
                : Array.Empty<string>();

            switch (command)
            {
                case "compile":
                    CompileFile(sourceFile: sourceFile, executeAfter: false,
                        programArgs: programArgs, noMain: false);
                    break;
                case "run":
                case "compileandrun":
                    CompileFile(sourceFile: sourceFile, executeAfter: true,
                        programArgs: programArgs, noMain: false);
                    break;
                case "check":
                    // Check mode: parse and analyze only, no executable generation required
                    // Useful for libraries, modules, or files without main()
                    CompileFile(sourceFile: sourceFile, executeAfter: false,
                        programArgs: programArgs, noMain: true);
                    break;
                default:
                    PrintUsage();
                    break;
            }
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine(value: "Usage:");
        Console.WriteLine(
            value: "  RazorForge <source-file>                          - Compile file (legacy)");
        Console.WriteLine(
            value: "  RazorForge compile <source-file>                  - Compile file");
        Console.WriteLine(
            value:
            "  RazorForge run <source-file> [args...]            - Compile and run file with optional arguments");
        Console.WriteLine(
            value:
            "  RazorForge compileandrun <source-file> [args...]  - Compile and run file with optional arguments");
        Console.WriteLine(
            value: "  RazorForge check <source-file>                    - Check file (no main required)");
        Console.WriteLine(
            value: "  RazorForge lsp                                    - Start language server");
        Console.WriteLine();
        Console.WriteLine(
            value: "  <source-file>: .rf file for RazorForge or .sf file for Suflae");
        Console.WriteLine(
            value: "  [args...]:     Optional arguments to pass to the compiled program");
    }

    private static void CompileFile(string sourceFile, bool executeAfter, string[] programArgs, bool noMain = false)
    {
        if (!File.Exists(path: sourceFile))
        {
            Console.WriteLine(value: $"Error: File '{sourceFile}' not found.");
            return;
        }

        string code = File.ReadAllText(path: sourceFile);
        Language language = sourceFile.EndsWith(value: ".sf")
            ? Language.Suflae
            : Language.RazorForge;
        LanguageMode mode = language == Language.Suflae
            ? LanguageMode.Suflae
            : LanguageMode.Normal;

        Console.WriteLine(value: $"Compiling {sourceFile} as {language} ({mode})...");
        Console.WriteLine();

        try
        {
            // Tokenize the code
            Console.WriteLine(value: "=== TOKENIZATION ===");
            List<Token> tokens = Tokenizer.Tokenize(source: code, language: language);
            Console.WriteLine(value: $"Generated {tokens.Count} tokens");
            // DEBUG: Print tokens
            if (sourceFile.Contains(value: "test_tokens"))
            {
                Console.WriteLine(value: "=== TOKENS ===");
                foreach (Token tok in tokens)
                {
                    Console.WriteLine(value: $"  {tok.Line}:{tok.Column} {tok.Type} '{tok.Text}'");
                }

                Console.WriteLine(value: "=== END TOKENS ===");
            }

            // Parse the code
            Console.WriteLine(value: "=== PARSING ===");
            BaseParser parser = language == Language.Suflae
                ? (BaseParser)new SuflaeParser(tokens: tokens, fileName: sourceFile)
                : new RazorForgeParser(tokens: tokens, fileName: sourceFile);
            Shared.AST.Program ast = parser.Parse();
            Console.WriteLine(
                value: $"Successfully parsed! AST contains {ast.Declarations.Count} declarations");

            // Semantic analysis
            Console.WriteLine(value: "=== SEMANTIC ANALYSIS ===");
            var analyzer =
                new SemanticAnalyzer(language: language, mode: mode, fileName: sourceFile);
            List<SemanticError> semanticErrors = analyzer.Analyze(program: ast);

            if (semanticErrors.Count > 0)
            {
                Console.WriteLine(value: $"Found {semanticErrors.Count} semantic errors:");
                foreach (SemanticError error in semanticErrors.Take(count: 10))
                {
                    string location = error.FileName != null
                        ? $"[{error.FileName}:{error.Location.Line}:{error.Location.Column}]"
                        : $"[{error.Location.Line}:{error.Location.Column}]";
                    Console.WriteLine(value: $"Semantic error{location}: {error.Message}");
                }

                if (semanticErrors.Count > 10)
                {
                    Console.WriteLine(value: $"  ... and {semanticErrors.Count - 10} more errors");
                }

                Console.WriteLine();

                // Don't continue if there are semantic errors
                if (!executeAfter)
                {
                    return;
                }
            }
            else
            {
                Console.WriteLine(value: "No semantic errors found!");
            }

            // Generate function variants (try_, check_, find_)
            Console.WriteLine(value: "=== FUNCTION VARIANT GENERATION ===");
            var variantGenerator = new FunctionVariantGenerator();
            variantGenerator.GenerateVariants(program: ast);

            if (variantGenerator.GeneratedVariants.Count > 0)
            {
                Console.WriteLine(
                    value:
                    $"Generated {variantGenerator.GeneratedVariants.Count} function variants:");
                foreach (FunctionDeclaration variant in variantGenerator.GeneratedVariants)
                {
                    Console.WriteLine(value: $"  - {variant.Name}()");
                }

                // Add generated variants to the AST
                var updatedDeclarations = new List<IAstNode>(collection: ast.Declarations);
                updatedDeclarations.AddRange(
                    collection: variantGenerator.GeneratedVariants.Cast<IAstNode>());
                ast = new Compilers.Shared.AST.Program(Declarations: updatedDeclarations,
                    Location: ast.Location);
            }
            else
            {
                Console.WriteLine(
                    value: "No function variants generated (no throw/absent detected)");
            }

            // Code generation
            Console.WriteLine(value: "=== CODE GENERATION ===");

            // Generate readable output
            var simpleCodeGen = new SimpleCodeGenerator(language: language, mode: mode);
            simpleCodeGen.Generate(program: ast);
            string outputFile = Path.ChangeExtension(path: sourceFile, extension: ".out");
            File.WriteAllText(path: outputFile, contents: simpleCodeGen.GetGeneratedCode());
            Console.WriteLine(value: $"Simple code written to: {outputFile}");

            // Generate LLVM IR
            var llvmCodeGen = new LLVMCodeGenerator(language: language, mode: mode);
            llvmCodeGen.SourceFileName = sourceFile;
            llvmCodeGen.Generate(program: ast);
            string llvmFile = Path.ChangeExtension(path: sourceFile, extension: ".ll");
            File.WriteAllText(path: llvmFile, contents: llvmCodeGen.GetGeneratedCode());
            Console.WriteLine(value: $"LLVM IR written to: {llvmFile}");

            // In noMain mode (check command), skip executable generation
            if (noMain)
            {
                Console.WriteLine();
                Console.WriteLine(value: "✅ Check successful! (no-main mode, executable not generated)");
            }
            else
            {
                // Complete bootstrap pipeline: compile to executable
                Console.WriteLine(value: "=== EXECUTABLE GENERATION ===");
                string? executablePath = GenerateExecutable(llvmFile: llvmFile);
                if (executablePath != null)
                {
                    Console.WriteLine(value: $"Executable generated: {executablePath}");

                    // If run command, execute the generated executable
                    if (executeAfter)
                    {
                        Console.WriteLine();
                        Console.WriteLine(value: "=== RUNNING PROGRAM ===");
                        Console.WriteLine();

                        RunExecutable(executablePath: executablePath, programArgs: programArgs);
                    }
                }
                else
                {
                    Console.WriteLine(
                        value: "Note: LLVM tools not available. Skipping executable generation.");
                    Console.WriteLine(
                        value: "To generate executables, install LLVM and ensure 'clang' is in PATH.");

                    if (executeAfter)
                    {
                        Console.WriteLine();
                        Console.WriteLine(value: "Cannot run program without executable.");
                    }
                }

                Console.WriteLine();
                Console.WriteLine(value: "✅ Compilation successful!");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine(value: $"❌ Compilation failed: {ex.Message}");
            if (ex.InnerException != null)
            {
                Console.WriteLine(value: $"   Inner: {ex.InnerException.Message}");
            }
        }
    }

    private static string? GenerateExecutable(string llvmFile)
    {
        try
        {
            string executablePath = Path.ChangeExtension(path: llvmFile, extension: ".exe");

            // Find the RazorForge runtime C source files
            string? projectRoot = FindProjectRoot(startPath: llvmFile);
            string runtimeSources = "";
            if (projectRoot != null)
            {
                string runtimeDir = Path.Combine(path1: projectRoot, path2: "native", path3: "runtime");
                string memoryC = Path.Combine(path1: runtimeDir, path2: "memory.c");
                string stacktraceC = Path.Combine(path1: runtimeDir, path2: "stacktrace.c");

                if (File.Exists(path: memoryC) && File.Exists(path: stacktraceC))
                {
                    runtimeSources = $"\"{memoryC}\" \"{stacktraceC}\"";
                }
            }

            // Use clang to compile LLVM IR to executable
            // On Windows, we need to link with legacy_stdio_definitions for printf/scanf
            // On Unix-like systems, libc is linked automatically
            string linkerFlags = OperatingSystem.IsWindows()
                ? "-Wno-override-module -llegacy_stdio_definitions"
                : "-Wno-override-module";

            var clangProcess = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "clang",
                Arguments = $"\"{llvmFile}\" {runtimeSources} -o \"{executablePath}\" {linkerFlags}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = System.Diagnostics.Process.Start(startInfo: clangProcess);
            if (process == null)
            {
                return null;
            }

            process.WaitForExit();

            if (process.ExitCode == 0 && File.Exists(path: executablePath))
            {
                return executablePath;
            }
            else
            {
                string error = process.StandardError.ReadToEnd();
                if (!string.IsNullOrEmpty(value: error))
                {
                    Console.WriteLine(value: $"Clang error: {error}");
                }

                return null;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine(value: $"Error during executable generation: {ex.Message}");
            return null;
        }
    }

    private static void RunExecutable(string executablePath, string[] programArgs)
    {
        try
        {
            var runProcess = new System.Diagnostics.ProcessStartInfo
            {
                FileName = executablePath,
                Arguments =
                    string.Join(separator: " ",
                        values: programArgs.Select(selector: arg => $"\"{arg}\"")),
                UseShellExecute = false,
                RedirectStandardOutput = false, // Let output go directly to console
                RedirectStandardError = false,
                RedirectStandardInput = false,
                CreateNoWindow = false
            };

            using var process = System.Diagnostics.Process.Start(startInfo: runProcess);
            if (process == null)
            {
                Console.WriteLine(value: "Failed to start executable.");
                return;
            }

            process.WaitForExit();

            Console.WriteLine();
            Console.WriteLine(value: $"Program exited with code: {process.ExitCode}");
        }
        catch (Exception ex)
        {
            Console.WriteLine(value: $"Error running executable: {ex.Message}");
        }
    }

    private static string? FindProjectRoot(string startPath)
    {
        // Walk up the directory tree looking for RazorForge.csproj or native/runtime directory
        string? directory = Path.GetDirectoryName(path: startPath);
        while (directory != null)
        {
            // Check for RazorForge.csproj
            if (File.Exists(path: Path.Combine(path1: directory, path2: "RazorForge.csproj")))
            {
                return directory;
            }

            // Check for native/runtime directory
            string runtimeDir = Path.Combine(path1: directory, path2: "native", path3: "runtime");
            if (Directory.Exists(path: runtimeDir))
            {
                return directory;
            }

            directory = Path.GetDirectoryName(path: directory);
        }

        return null;
    }
}
