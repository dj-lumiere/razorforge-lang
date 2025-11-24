using Compilers.Shared.Parser;
using Compilers.Shared.Lexer;
using Compilers.Shared.Analysis;
using Compilers.Shared.CodeGen;
using Compilers.RazorForge.Parser;
using Compilers.Suflae.Parser;

namespace Compilers;

internal class Program
{
    public static void Main(string[] args)
    {
        if (args.Length != 1)
        {
            Console.WriteLine(value: "Usage: RazorForge <source-file>");
            Console.WriteLine(
                value: "  <source-file>: .rf file for RazorForge or .sf file for Suflae");
            return;
        }

        string sourceFile = args[0];
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

            // Parse the code
            Console.WriteLine(value: "=== PARSING ===");
            BaseParser parser = language == Language.Suflae
                ? (BaseParser)new SuflaeParser(tokens: tokens)
                : new RazorForgeParser(tokens: tokens);
            Shared.AST.Program ast = parser.Parse();
            Console.WriteLine(
                value: $"Successfully parsed! AST contains {ast.Declarations.Count} declarations");

            // Semantic analysis
            Console.WriteLine(value: "=== SEMANTIC ANALYSIS ===");
            var analyzer = new SemanticAnalyzer(language: language, mode: mode);
            List<SemanticError> semanticErrors = analyzer.Analyze(program: ast);

            if (semanticErrors.Count > 0)
            {
                Console.WriteLine(value: $"Found {semanticErrors.Count} semantic errors:");
                foreach (SemanticError error in semanticErrors.Take(count: 10))
                {
                    Console.WriteLine(value: $"  - {error.Message} at line {error.Location.Line}");
                }

                if (semanticErrors.Count > 10)
                {
                    Console.WriteLine(value: $"  ... and {semanticErrors.Count - 10} more errors");
                }

                Console.WriteLine();
            }
            else
            {
                Console.WriteLine(value: "No semantic errors found!");
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
            llvmCodeGen.Generate(program: ast);
            string llvmFile = Path.ChangeExtension(path: sourceFile, extension: ".ll");
            File.WriteAllText(path: llvmFile, contents: llvmCodeGen.GetGeneratedCode());
            Console.WriteLine(value: $"LLVM IR written to: {llvmFile}");

            // Complete bootstrap pipeline: compile to executable
            Console.WriteLine(value: "=== EXECUTABLE GENERATION ===");
            string? executablePath = GenerateExecutable(llvmFile: llvmFile);
            if (executablePath != null)
            {
                Console.WriteLine(value: $"Executable generated: {executablePath}");
            }
            else
            {
                Console.WriteLine(
                    value: "Note: LLVM tools not available. Skipping executable generation.");
                Console.WriteLine(
                    value: "To generate executables, install LLVM and ensure 'clang' is in PATH.");
            }

            Console.WriteLine();
            Console.WriteLine(value: "✅ Compilation successful!");
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

            // Use clang to compile LLVM IR to executable
            var clangProcess = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "clang",
                Arguments = $"\"{llvmFile}\" -o \"{executablePath}\"",
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
}
