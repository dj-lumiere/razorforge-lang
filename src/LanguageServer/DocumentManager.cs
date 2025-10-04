using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Compilers.Shared.Analysis;
using Compilers.Shared.AST;

namespace RazorForge.LanguageServer
{
    /// <summary>
    /// Manages the lifecycle and state of open documents in the Language Server.
    /// Provides centralized document tracking, content synchronization, and analysis coordination.
    ///
    /// This class handles:
    /// - Document registration and deregistration
    /// - Incremental text change application
    /// - Automatic semantic analysis triggering
    /// - Document state caching for performance
    /// - Thread-safe access to document collection
    ///
    /// Documents are identified by their URI and tracked with version numbers
    /// to ensure consistency between client and server state.
    /// </summary>
    public class DocumentManager
    {
        /// <summary>
        /// Logger for capturing document management events and diagnostics.
        /// </summary>
        private readonly ILogger<DocumentManager> _logger;

        /// <summary>
        /// Thread-safe collection of open documents indexed by URI.
        /// Stores the current state and analysis results for each document.
        /// </summary>
        private readonly ConcurrentDictionary<string, DocumentState> _documents;

        /// <summary>
        /// RazorForge compiler service for performing semantic analysis.
        /// Used to analyze document content and generate diagnostics.
        /// </summary>
        private readonly IRazorForgeCompilerService _compilerService;

        /// <summary>
        /// Initializes a new instance of the DocumentManager class.
        /// </summary>
        /// <param name="logger">Logger for capturing events and diagnostics</param>
        /// <param name="compilerService">Compiler service for document analysis</param>
        public DocumentManager(ILogger<DocumentManager> logger, IRazorForgeCompilerService compilerService)
        {
            _logger = logger;
            _compilerService = compilerService;
            _documents = new ConcurrentDictionary<string, DocumentState>();
        }

        /// <summary>
        /// Handles document open events from the LSP client.
        /// Registers a new document for tracking and triggers initial analysis.
        ///
        /// Creates a DocumentState record with the provided content and metadata,
        /// adds it to the document collection, and initiates semantic analysis.
        /// </summary>
        /// <param name="uri">Unique identifier for the document</param>
        /// <param name="languageId">Language identifier (should be 'razorforge')</param>
        /// <param name="version">Initial version number from the client</param>
        /// <param name="text">Initial content of the document</param>
        public void OpenDocument(string uri, string languageId, int version, string text)
        {
            _logger.LogInformation($"Opening document: {uri}");

            var document = new DocumentState
            {
                Uri = uri,
                LanguageId = languageId,
                Version = version,
                Text = text,
                LastModified = DateTime.UtcNow
            };

            _documents.AddOrUpdate(uri, document, (_, _) => document);

            // Trigger initial analysis
            AnalyzeDocument(document);
        }

        /// <summary>
        /// Handles document change events from the LSP client.
        /// Applies incremental changes to the document content and triggers re-analysis.
        ///
        /// Supports both full document replacement and incremental changes.
        /// Incremental changes are applied in sequence to maintain document consistency.
        /// After applying changes, triggers semantic analysis on the updated content.
        /// </summary>
        /// <param name="uri">URI of the document being changed</param>
        /// <param name="version">New version number from the client</param>
        /// <param name="changes">Collection of text changes to apply</param>
        public void ChangeDocument(string uri, int version, IEnumerable<TextDocumentContentChangeEvent> changes)
        {
            if (!_documents.TryGetValue(uri, out var document))
            {
                _logger.LogWarning($"Attempted to change unknown document: {uri}");
                return;
            }

            _logger.LogDebug($"Document changed: {uri}, version: {version}");

            // Apply incremental changes
            var newText = ApplyChanges(document.Text, changes);

            var updatedDocument = document with
            {
                Version = version,
                Text = newText,
                LastModified = DateTime.UtcNow
            };

            _documents.AddOrUpdate(uri, updatedDocument, (_, _) => updatedDocument);

            // Trigger re-analysis
            AnalyzeDocument(updatedDocument);
        }

        /// <summary>
        /// Handles document close events from the LSP client.
        /// Removes the document from tracking and cleans up associated resources.
        ///
        /// Once a document is closed, it will no longer receive analysis updates
        /// and its state will be removed from memory.
        /// </summary>
        /// <param name="uri">URI of the document being closed</param>
        public void CloseDocument(string uri)
        {
            _logger.LogInformation($"Closing document: {uri}");
            _documents.TryRemove(uri, out _);
        }

        /// <summary>
        /// Retrieves the current state of a document by URI.
        /// Returns null if the document is not currently open or tracked.
        ///
        /// The returned DocumentState includes the current text content,
        /// version information, and the latest compilation results.
        /// </summary>
        /// <param name="uri">URI of the document to retrieve</param>
        /// <returns>Current document state, or null if not found</returns>
        public DocumentState? GetDocument(string uri)
        {
            return _documents.TryGetValue(uri, out var document) ? document : null;
        }

        /// <summary>
        /// Retrieves all currently open documents.
        /// Returns a snapshot of all tracked documents at the time of call.
        ///
        /// Useful for operations that need to process all open RazorForge files,
        /// such as workspace-wide analysis or cleanup operations.
        /// </summary>
        /// <returns>Collection of all currently tracked document states</returns>
        public IEnumerable<DocumentState> GetAllDocuments()
        {
            return _documents.Values.ToList();
        }

        /// <summary>
        /// Performs semantic analysis on a document and updates its state.
        /// Integrates with the RazorForge compiler pipeline to generate diagnostics.
        ///
        /// The analysis process:
        /// 1. Invokes the compiler service with current document content
        /// 2. Receives compilation results including errors and symbols
        /// 3. Updates the document state with analysis results
        /// 4. Logs analysis completion and error count
        ///
        /// Analysis is performed asynchronously and errors are logged but do not
        /// prevent the document from remaining in the tracked collection.
        /// </summary>
        /// <param name="document">Document state to analyze</param>
        private void AnalyzeDocument(DocumentState document)
        {
            try
            {
                _logger.LogDebug($"Analyzing document: {document.Uri}");

                // Compile and analyze the document
                var result = _compilerService.AnalyzeCode(document.Text, document.Uri);

                var updatedDocument = document with
                {
                    CompilationResult = result,
                    LastAnalyzed = DateTime.UtcNow
                };

                _documents.AddOrUpdate(document.Uri, updatedDocument, (_, _) => updatedDocument);

                _logger.LogDebug($"Analysis complete: {document.Uri}, errors: {result.Errors.Count}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to analyze document: {document.Uri}");
            }
        }

        /// <summary>
        /// Applies a sequence of text changes to document content.
        /// Handles both full document replacement and incremental edits.
        ///
        /// For incremental changes, the method:
        /// 1. Splits the text into lines for position-based editing
        /// 2. Calculates the affected range using line/character positions
        /// 3. Reconstructs the text with changes applied
        /// 4. Handles multi-line changes and boundary conditions
        ///
        /// Full document replacement occurs when a change has no range specified.
        /// Changes are applied in the order provided by the client.
        /// </summary>
        /// <param name="originalText">Current document content</param>
        /// <param name="changes">Sequence of changes to apply</param>
        /// <returns>Updated document content after applying all changes</returns>
        private string ApplyChanges(string originalText, IEnumerable<TextDocumentContentChangeEvent> changes)
        {
            var text = originalText;

            foreach (var change in changes)
            {
                if (change.Range == null)
                {
                    // Full document replacement
                    text = change.Text;
                }
                else
                {
                    // Incremental change
                    var lines = text.Split('\n');
                    var startLine = (int)change.Range.Start.Line;
                    var startChar = (int)change.Range.Start.Character;
                    var endLine = (int)change.Range.End.Line;
                    var endChar = (int)change.Range.End.Character;

                    // Build new text with the change applied
                    var newLines = new List<string>();

                    // Add lines before the change
                    for (int i = 0; i < startLine; i++)
                    {
                        if (i < lines.Length)
                            newLines.Add(lines[i]);
                    }

                    // Handle the changed line(s)
                    if (startLine < lines.Length)
                    {
                        var beforeChange = startLine < lines.Length ? lines[startLine].Substring(0, Math.Min(startChar, lines[startLine].Length)) : "";
                        var afterChange = endLine < lines.Length ? lines[endLine].Substring(Math.Min(endChar, lines[endLine].Length)) : "";

                        var changedText = beforeChange + change.Text + afterChange;
                        var changedLines = changedText.Split('\n');

                        newLines.AddRange(changedLines);
                    }
                    else
                    {
                        // Change is beyond current text
                        newLines.Add(change.Text);
                    }

                    // Add lines after the change
                    for (int i = endLine + 1; i < lines.Length; i++)
                    {
                        newLines.Add(lines[i]);
                    }

                    text = string.Join("\n", newLines);
                }
            }

            return text;
        }
    }

    /// <summary>
    /// Represents the complete state of a document managed by the Language Server.
    /// This immutable record contains all information needed to track and analyze
    /// a RazorForge document throughout its lifecycle.
    ///
    /// The record includes:
    /// - Document identification and metadata (URI, language, version)
    /// - Current text content and modification timestamps
    /// - Latest compilation results and analysis data
    ///
    /// Since this is a record type, updates create new instances preserving immutability
    /// while allowing efficient updates through the 'with' expression syntax.
    /// </summary>
    public record DocumentState
    {
        /// <summary>
        /// Unique identifier for the document (typically a file path or URI).
        /// Used by the LSP client and server to reference the same document.
        /// </summary>
        public string Uri { get; init; } = null!;

        /// <summary>
        /// Language identifier for the document (should be 'razorforge').
        /// Used by the client to determine syntax highlighting and language features.
        /// </summary>
        public string LanguageId { get; init; } = "";

        /// <summary>
        /// Version number of the document as provided by the client.
        /// Incremented with each change to ensure synchronization between client and server.
        /// </summary>
        public int Version { get; init; }

        /// <summary>
        /// Current text content of the document.
        /// Updated through incremental changes or full document replacement.
        /// </summary>
        public string Text { get; init; } = "";

        /// <summary>
        /// Timestamp when the document was last modified.
        /// Updated whenever the text content changes.
        /// </summary>
        public DateTime LastModified { get; init; }

        /// <summary>
        /// Timestamp when the document was last analyzed by the compiler.
        /// Used to track analysis freshness and performance metrics.
        /// </summary>
        public DateTime LastAnalyzed { get; init; }

        /// <summary>
        /// Latest compilation results from semantic analysis.
        /// Contains errors, symbols, and other analysis data, or null if analysis failed.
        /// </summary>
        public CompilationResult? CompilationResult { get; init; }
    }

    /// <summary>
    /// Contains the complete results of compiling and analyzing RazorForge source code.
    /// This record aggregates all outputs from the RazorForge compiler pipeline
    /// including syntax analysis, semantic analysis, and symbol extraction.
    ///
    /// Used by the Language Server to:
    /// - Generate diagnostic information for the client
    /// - Provide completion suggestions and symbol information
    /// - Support hover tooltips and go-to-definition features
    /// - Enable code navigation and refactoring tools
    /// </summary>
    public record CompilationResult
    {
        /// <summary>
        /// Abstract Syntax Tree representing the parsed structure of the RazorForge code.
        /// Null if parsing failed due to syntax errors.
        /// </summary>
        public Program? AST { get; init; }

        /// <summary>
        /// Collection of semantic errors found during analysis.
        /// Includes syntax errors, type errors, and other compilation issues.
        /// </summary>
        public List<SemanticError> Errors { get; init; } = new();

        /// <summary>
        /// Pre-computed completion items for efficient autocomplete responses.
        /// Generated from symbols and language constructs found in the code.
        /// </summary>
        public List<CompletionItem> CompletionItems { get; init; } = new();

        /// <summary>
        /// Symbols indexed by line number for quick position-based lookups.
        /// Used for hover information and go-to-definition functionality.
        /// </summary>
        public Dictionary<int, List<Symbol>> SymbolsByLine { get; init; } = new();

        /// <summary>
        /// Indicates whether the compilation completed without errors.
        /// True if no semantic errors were found, false otherwise.
        /// </summary>
        public bool IsValid { get; init; }
    }

    /// <summary>
    /// Represents a symbol (identifier) found in RazorForge source code.
    /// Symbols include functions, variables, types, classes, and other named entities.
    ///
    /// Used by the Language Server to provide:
    /// - Intelligent completion suggestions
    /// - Hover documentation and type information
    /// - Symbol-based navigation (go-to-definition, find references)
    /// - Code outline and symbol search functionality
    ///
    /// Each symbol contains both its declaration information and its location
    /// in the source code for precise editor integration.
    /// </summary>
    public record Symbol
    {
        /// <summary>
        /// The identifier name of the symbol as it appears in source code.
        /// </summary>
        public string Name { get; init; } = "";

        /// <summary>
        /// Type information for the symbol (e.g., 'i32', 'string', 'class MyClass').
        /// For functions, includes return type; for variables, includes data type.
        /// </summary>
        public string Type { get; init; } = "";

        /// <summary>
        /// Human-readable description of the symbol for documentation purposes.
        /// Includes signature information for functions and usage context.
        /// </summary>
        public string Description { get; init; } = "";

        /// <summary>
        /// Source location where the symbol is defined or declared.
        /// Used for navigation features and position-based symbol lookups.
        /// </summary>
        public SourceLocation Location { get; init; } = new(0, 0, 0);

        /// <summary>
        /// Classification of the symbol type (function, variable, class, etc.).
        /// Used to determine appropriate icons and completion behavior in the IDE.
        /// </summary>
        public SymbolKind Kind { get; init; }
    }

    /// <summary>
    /// Enumeration of different symbol types found in RazorForge code.
    /// Used for categorization, icon selection, and completion filtering in IDEs.
    /// </summary>
    public enum SymbolKind
    {
        /// <summary>A standalone function or recipe declaration.</summary>
        Function,

        /// <summary>A variable declaration (let, var, const).</summary>
        Variable,

        /// <summary>A type alias or type definition.</summary>
        Type,

        /// <summary>A class declaration.</summary>
        Entity,

        /// <summary>A struct declaration.</summary>
        Record,

        /// <summary>An enum declaration.</summary>
        Option,

        /// <summary>A property within a class or struct.</summary>
        Property,

        /// <summary>A method within a class.</summary>
        Method,

        /// <summary>A function or method parameter.</summary>
        Parameter
    }
}