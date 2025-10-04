using Compilers.Shared.Parser;
using Compilers.Shared.Lexer;
using Compilers.Shared.Analysis;
using Compilers.Shared.CodeGen;
using Compilers.RazorForge.Parser;
using Compilers.Cake.Parser;

namespace Compilers;

class Program
{
    public static void Main(string[] args)
    {
        if (args.Length != 1)
        {
            Console.WriteLine("Usage: RazorForge <source-file>");
            Console.WriteLine("  <source-file>: .rf file for RazorForge or .cake file for Cake");
            return;
        }

        var sourceFile = args[0];
        if (!File.Exists(sourceFile))
        {
            Console.WriteLine($"Error: File '{sourceFile}' not found.");
            return;
        }

        var code = File.ReadAllText(sourceFile);
        var language = sourceFile.EndsWith(".cake") ? Language.Cake : Language.RazorForge;
        var mode = language == Language.Cake ? LanguageMode.Sweet : LanguageMode.Normal;
        
        Console.WriteLine($"Compiling {sourceFile} as {language} ({mode})...");
        Console.WriteLine();
        
        try
        {
            // Tokenize the code
            Console.WriteLine("=== TOKENIZATION ===");
            var tokens = Tokenizer.Tokenize(code, language);
            Console.WriteLine($"Generated {tokens.Count} tokens");
            
            // Parse the code
            Console.WriteLine("=== PARSING ===");
            var parser = language == Language.Cake 
                ? (BaseParser)new CakeParser(tokens)
                : new RazorForgeParser(tokens);
            var ast = parser.Parse();
            Console.WriteLine($"Successfully parsed! AST contains {ast.Declarations.Count} declarations");
            
            // Semantic analysis
            Console.WriteLine("=== SEMANTIC ANALYSIS ===");
            var analyzer = new SemanticAnalyzer(language, mode);
            var semanticErrors = analyzer.Analyze(ast);

            if (semanticErrors.Count > 0)
            {
                Console.WriteLine($"Found {semanticErrors.Count} semantic errors:");
                foreach (var error in semanticErrors.Take(10))
                {
                    Console.WriteLine($"  - {error.Message} at line {error.Location.Line}");
                }
                if (semanticErrors.Count > 10)
                {
                    Console.WriteLine($"  ... and {semanticErrors.Count - 10} more errors");
                }
                Console.WriteLine();
            }
            else
            {
                Console.WriteLine("No semantic errors found!");
            }
            
            // Code generation
            Console.WriteLine("=== CODE GENERATION ===");
            
            // Generate readable output
            var simpleCodeGen = new SimpleCodeGenerator(language, mode);
            simpleCodeGen.Generate(ast);
            var outputFile = Path.ChangeExtension(sourceFile, ".out");
            File.WriteAllText(outputFile, simpleCodeGen.GetGeneratedCode());
            Console.WriteLine($"Simple code written to: {outputFile}");
            
            // Generate LLVM IR
            var llvmCodeGen = new LLVMCodeGenerator(language, mode);
            llvmCodeGen.Generate(ast);
            var llvmFile = Path.ChangeExtension(sourceFile, ".ll");
            File.WriteAllText(llvmFile, llvmCodeGen.GetGeneratedCode());
            Console.WriteLine($"LLVM IR written to: {llvmFile}");
            
            // Complete bootstrap pipeline: compile to executable
            Console.WriteLine("=== EXECUTABLE GENERATION ===");
            var executablePath = GenerateExecutable(llvmFile);
            if (executablePath != null)
            {
                Console.WriteLine($"Executable generated: {executablePath}");
            }
            else
            {
                Console.WriteLine("Note: LLVM tools not available. Skipping executable generation.");
                Console.WriteLine("To generate executables, install LLVM and ensure 'clang' is in PATH.");
            }
            
            Console.WriteLine();
            Console.WriteLine("✅ Compilation successful!");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Compilation failed: {ex.Message}");
            if (ex.InnerException != null)
            {
                Console.WriteLine($"   Inner: {ex.InnerException.Message}");
            }
        }
    }
    
    private static string? GenerateExecutable(string llvmFile)
    {
        try
        {
            var executablePath = Path.ChangeExtension(llvmFile, ".exe");
            
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
            
            using var process = System.Diagnostics.Process.Start(clangProcess);
            if (process == null) return null;
            
            process.WaitForExit();
            
            if (process.ExitCode == 0 && File.Exists(executablePath))
            {
                return executablePath;
            }
            else
            {
                var error = process.StandardError.ReadToEnd();
                if (!string.IsNullOrEmpty(error))
                {
                    Console.WriteLine($"Clang error: {error}");
                }
                return null;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error during executable generation: {ex.Message}");
            return null;
        }
    }
}