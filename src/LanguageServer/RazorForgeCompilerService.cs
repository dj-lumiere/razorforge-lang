using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Compilers.Shared.Analysis;
using Compilers.Shared.AST;
using Compilers.Shared.Lexer;
using Compilers.RazorForge.Parser;
using Compilers.RazorForge.Lexer;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace RazorForge.LanguageServer;

/// <summary>
/// Implementation of RazorForge compiler services for Language Server
/// </summary>
public class RazorForgeCompilerService : IRazorForgeCompilerService
{
    private readonly ILogger<RazorForgeCompilerService> _logger;
    private readonly SemanticAnalyzer _semanticAnalyzer;

    /// <summary>
    /// Provides services for compiling and analyzing RazorForge code, to support
    /// Language Server functionalities such as code analysis, auto-completion,
    /// hover information, and symbol retrieval.
    /// </summary>
    public RazorForgeCompilerService(ILogger<RazorForgeCompilerService> logger,
        SemanticAnalyzer semanticAnalyzer)
    {
        _logger = logger;
        _semanticAnalyzer = semanticAnalyzer;
    }

    /// <summary>
    /// Analyze RazorForge code and return compilation results
    /// </summary>
    public CompilationResult AnalyzeCode(string code, string filePath)
    {
        try
        {
            _logger.LogDebug(message: $"Analyzing code from {filePath}");

            // Tokenize
            List<Token> tokens = Tokenizer.Tokenize(source: code, language: Language.RazorForge);

            // Parse
            var parser = new RazorForgeParser(tokens: tokens);
            Program ast = parser.Parse();

            // Semantic analysis
            List<SemanticError> errors = _semanticAnalyzer.Analyze(program: ast);

            // Extract symbols for completion and navigation
            List<Symbol> symbols = ExtractSymbols(ast: ast);
            var symbolsByLine = symbols.GroupBy(keySelector: s => s.Location.Line)
                                       .ToDictionary(keySelector: g => g.Key,
                                            elementSelector: g => g.ToList());

            // Generate completion items
            List<CompletionItem> completionItems = GenerateCompletionItems(symbols: symbols);

            return new CompilationResult
            {
                AST = ast,
                Errors = errors,
                CompletionItems = completionItems,
                SymbolsByLine = symbolsByLine,
                IsValid = errors.Count == 0
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(exception: ex, message: $"Failed to analyze code from {filePath}");

            return new CompilationResult
            {
                Errors = new List<SemanticError>
                {
                    new(Message: $"Compilation failed: {ex.Message}",
                        Location: new SourceLocation(Line: 0, Column: 0, Position: 0))
                },
                IsValid = false
            };
        }
    }

    /// <summary>
    /// Get completion suggestions for position in code
    /// </summary>
    public List<CompletionSuggestion> GetCompletions(string code, int line, int column)
    {
        var suggestions = new List<CompletionSuggestion>();

        try
        {
            // Analyze code to get context
            CompilationResult result = AnalyzeCode(code: code, filePath: "temp");

            // Add language keywords
            suggestions.AddRange(collection: GetKeywordCompletions());

            // Add symbols from current file
            if (result.AST != null)
            {
                suggestions.AddRange(collection: GetSymbolCompletions(ast: result.AST));
            }

            // Add built-in types and functions
            suggestions.AddRange(collection: GetBuiltInCompletions());

            _logger.LogDebug(
                message:
                $"Generated {suggestions.Count} completion suggestions for line {line}, column {column}");
        }
        catch (Exception ex)
        {
            _logger.LogError(exception: ex,
                message: $"Failed to get completions for line {line}, column {column}");
        }

        return suggestions;
    }

    /// <summary>
    /// Get hover information for symbol at position
    /// </summary>
    public HoverInfo? GetHoverInfo(string code, int line, int column)
    {
        try
        {
            CompilationResult result = AnalyzeCode(code: code, filePath: "temp");

            if (result.SymbolsByLine.TryGetValue(key: line, value: out List<Symbol>? symbols))
            {
                // Find symbol at or near the column
                Symbol? symbol =
                    symbols.FirstOrDefault(); // Simplified - would need better position matching

                if (symbol != null)
                {
                    return new HoverInfo
                    {
                        Content = $"**{symbol.Name}**\n\n{symbol.Description}",
                        Type = symbol.Type,
                        Documentation = symbol.Description,
                        Location = symbol.Location
                    };
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(exception: ex,
                message: $"Failed to get hover info for line {line}, column {column}");
        }

        return null;
    }

    /// <summary>
    /// Get all symbols in code for navigation
    /// </summary>
    public List<Symbol> GetSymbols(string code)
    {
        try
        {
            CompilationResult result = AnalyzeCode(code: code, filePath: "temp");
            return result.SymbolsByLine
                         .Values
                         .SelectMany(selector: s => s)
                         .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(exception: ex, message: "Failed to extract symbols");
            return new List<Symbol>();
        }
    }

    /// <summary>
    /// Extract symbols from AST for completion and navigation
    /// </summary>
    private List<Symbol> ExtractSymbols(Program ast)
    {
        var symbols = new List<Symbol>();

        foreach (IAstNode declaration in ast.Declarations)
        {
            switch (declaration)
            {
                case FunctionDeclaration func:
                    symbols.Add(item: new Symbol
                    {
                        Name = func.Name,
                        Type = func.ReturnType?.ToString() ?? "void",
                        Description =
                            $"Function {func.Name}({string.Join(separator: ", ", values: func.Parameters.Select(selector: p => $"{p.Name}: {p.Type}"))})",
                        Location = func.Location,
                        Kind = SymbolKind.Function
                    });

                    // Add parameters as symbols
                    foreach (Parameter param in func.Parameters)
                    {
                        symbols.Add(item: new Symbol
                        {
                            Name = param.Name,
                            Type = param.Type?.ToString() ?? "unknown",
                            Description = $"Parameter {param.Name}",
                            Location = param.Location,
                            Kind = SymbolKind.Parameter
                        });
                    }

                    break;

                case ClassDeclaration cls:
                    symbols.Add(item: new Symbol
                    {
                        Name = cls.Name,
                        Type = "class",
                        Description = $"Entity {cls.Name}",
                        Location = cls.Location,
                        Kind = SymbolKind.Entity
                    });
                    break;

                case StructDeclaration str:
                    symbols.Add(item: new Symbol
                    {
                        Name = str.Name,
                        Type = "struct",
                        Description = $"Record {str.Name}",
                        Location = str.Location,
                        Kind = SymbolKind.Record
                    });
                    break;

                case VariableDeclaration var:
                    symbols.Add(item: new Symbol
                    {
                        Name = var.Name,
                        Type = var.Type?.ToString() ?? "auto",
                        Description = $"Variable {var.Name}",
                        Location = var.Location,
                        Kind = SymbolKind.Variable
                    });
                    break;
            }
        }

        return symbols;
    }

    /// <summary>
    /// Generate completion items from symbols
    /// </summary>
    private List<CompletionItem> GenerateCompletionItems(List<Symbol> symbols)
    {
        return symbols.Select(selector: symbol => new CompletionItem
                       {
                           Label = symbol.Name,
                           Detail = symbol.Type,
                           Documentation = symbol.Description,
                           Kind = MapSymbolKindToCompletionKind(symbolKind: symbol.Kind)
                       })
                      .ToList();
    }

    /// <summary>
    /// Get RazorForge language keyword completions
    /// </summary>
    private List<CompletionSuggestion> GetKeywordCompletions()
    {
        string[] keywords = new[]
        {
            "routine",
            "entity",
            "record",
            "variant",
            "kind",
            "feature",
            "let",
            "var",
            "preset",
            "if",
            "else",
            "when",
            "while",
            "for",
            "in",
            "to",
            "by",
            "when",
            "default",
            "break",
            "continue",
            "return",
            "danger",
            "external",
            "import",
            "export",
            "using",
            "true",
            "false",
            "none",
            "s8",
            "s16",
            "s32",
            "s64",
            "s128",
            "u8",
            "u16",
            "u32",
            "u64",
            "u128",
            "f16",
            "f32",
            "f64",
            "f128",
            "d32",
            "d64",
            "d128",
            "bool",
            "letter",
            "Text",
            "void",
            "any",
            "sysuint"
        };

        return keywords.Select(selector: keyword => new CompletionSuggestion
                        {
                            Label = keyword,
                            Detail = "keyword",
                            Documentation = $"RazorForge keyword: {keyword}",
                            Kind = CompletionKind.Keyword,
                            InsertText = keyword
                        })
                       .ToList();
    }

    /// <summary>
    /// Get symbol completions from AST
    /// </summary>
    private List<CompletionSuggestion> GetSymbolCompletions(Program ast)
    {
        List<Symbol> symbols = ExtractSymbols(ast: ast);

        return symbols.Select(selector: symbol => new CompletionSuggestion
                       {
                           Label = symbol.Name,
                           Detail = symbol.Type,
                           Documentation = symbol.Description,
                           Kind = MapSymbolKindToCompletionKindInternal(
                               symbolKind: symbol.Kind),
                           InsertText = symbol.Name
                       })
                      .ToList();
    }

    /// <summary>
    /// Get built-in function and type completions
    /// </summary>
    private List<CompletionSuggestion> GetBuiltInCompletions()
    {
        var builtins = new List<CompletionSuggestion>
        {
            new()
            {
                Label = "DynamicSlice",
                Detail = "constructor",
                Documentation = "Create a heap-allocated memory slice",
                Kind = CompletionKind.Constructor,
                InsertText = "DynamicSlice($1)",
                IsSnippet = true
            },
            new()
            {
                Label = "TemporarySlice",
                Detail = "constructor",
                Documentation = "Create a stack-allocated memory slice",
                Kind = CompletionKind.Constructor,
                InsertText = "TemporarySlice($1)",
                IsSnippet = true
            },
            new()
            {
                Label = "write_as",
                Detail = "function",
                Documentation = "Write typed data to memory address (danger zone)",
                Kind = CompletionKind.Function,
                InsertText = "write_as<$1>!($2, $3)",
                IsSnippet = true
            },
            new()
            {
                Label = "read_as",
                Detail = "function",
                Documentation = "Read typed data from memory address (danger zone)",
                Kind = CompletionKind.Function,
                InsertText = "read_as<$1>!($2)",
                IsSnippet = true
            },
            new()
            {
                Label = "address_of",
                Detail = "function",
                Documentation = "Get address of variable (danger zone)",
                Kind = CompletionKind.Function,
                InsertText = "address_of!($1)",
                IsSnippet = true
            },
            new()
            {
                Label = "invalidate",
                Detail = "function",
                Documentation = "Free memory slice (danger zone)",
                Kind = CompletionKind.Function,
                InsertText = "invalidate!($1)",
                IsSnippet = true
            }
        };

        return builtins;
    }

    /// <summary>
    /// Map symbol kinds to LSP completion kinds
    /// </summary>
    private CompletionItemKind MapSymbolKindToCompletionKind(SymbolKind symbolKind)
    {
        return symbolKind switch
        {
            SymbolKind.Function => CompletionItemKind.Function,
            SymbolKind.Variable => CompletionItemKind.Variable,
            SymbolKind.Type => CompletionItemKind.TypeParameter,
            SymbolKind.Entity => CompletionItemKind.Class,
            SymbolKind.Record => CompletionItemKind.Struct,
            SymbolKind.Option => CompletionItemKind.Enum,
            SymbolKind.Property => CompletionItemKind.Property,
            SymbolKind.Method => CompletionItemKind.Method,
            SymbolKind.Parameter => CompletionItemKind.Variable,
            _ => CompletionItemKind.Text
        };
    }

    private CompletionKind MapSymbolKindToCompletionKindInternal(SymbolKind symbolKind)
    {
        return symbolKind switch
        {
            SymbolKind.Function => CompletionKind.Function,
            SymbolKind.Variable => CompletionKind.Variable,
            SymbolKind.Type => CompletionKind.Entity,
            SymbolKind.Entity => CompletionKind.Entity,
            SymbolKind.Record => CompletionKind.Record,
            SymbolKind.Option => CompletionKind.Enum,
            SymbolKind.Property => CompletionKind.Property,
            SymbolKind.Method => CompletionKind.Method,
            SymbolKind.Parameter => CompletionKind.Variable,
            _ => CompletionKind.Text
        };
    }
}
