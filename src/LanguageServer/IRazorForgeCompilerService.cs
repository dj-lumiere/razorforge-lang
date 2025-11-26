using System.Collections.Generic;
using Compilers.Shared.Analysis;
using Compilers.Shared.AST;

namespace RazorForge.LanguageServer;

/// <summary>
/// Interface defining compiler services for RazorForge Language Server integration.
/// Provides the contract for integrating the RazorForge compiler pipeline with LSP features.
///
/// This interface abstracts the compiler functionality needed for IDE integration:
/// - Code analysis and error detection
/// - Symbol extraction and completion generation
/// - Hover information and documentation lookup
/// - Semantic analysis integration
///
/// Implementations should integrate with the existing RazorForge compiler components
/// including the tokenizer, parser, and semantic analyzer.
/// </summary>
public interface IRazorForgeCompilerService
{
    /// <summary>
    /// Analyzes RazorForge source code and returns comprehensive compilation results.
    /// This is the primary entry point for code analysis in the Language Server.
    ///
    /// The analysis process includes:
    /// 1. Tokenization of the source code
    /// 2. Parsing into an Abstract Syntax Tree (AST)
    /// 3. Semantic analysis for type checking and error detection
    /// 4. Symbol extraction for IDE features
    /// 5. Generation of completion items
    ///
    /// Results include all information needed for LSP features like diagnostics,
    /// completion, hover, and navigation.
    /// </summary>
    /// <param name="code">RazorForge source code to analyze</param>
    /// <param name="filePath">Path to the source file (for error reporting)</param>
    /// <returns>Compilation results including AST, errors, and symbols</returns>
    CompilationResult AnalyzeCode(string code, string filePath);

    /// <summary>
    /// Generates context-aware completion suggestions for a specific position in code.
    /// Analyzes the code context at the given line and column to provide relevant suggestions.
    ///
    /// Completion sources include:
    /// - RazorForge language keywords (routine, class, let, etc.)
    /// - Built-in types and functions (DynamicSlice, TemporarySlice, etc.)
    /// - User-defined symbols (functions, variables, types)
    /// - Context-sensitive suggestions based on parsing state
    ///
    /// The suggestions are ranked by relevance and include snippet templates
    /// for complex constructs like function signatures.
    /// </summary>
    /// <param name="code">Source code to analyze for completion context</param>
    /// <param name="line">Zero-based line number of cursor position</param>
    /// <param name="column">Zero-based column number of cursor position</param>
    /// <returns>List of completion suggestions appropriate for the context</returns>
    List<CompletionSuggestion> GetCompletions(string code, int line, int column);

    /// <summary>
    /// Retrieves hover information for a symbol at the specified position.
    /// Analyzes the code to identify the symbol under the cursor and provides
    /// documentation, type information, and usage details.
    ///
    /// Hover information includes:
    /// - Symbol name and type signature
    /// - Documentation from comments or built-in descriptions
    /// - Parameter information for functions
    /// - Value information for constants
    ///
    /// Returns null if no symbol is found at the specified position.
    /// </summary>
    /// <param name="code">Source code containing the symbol</param>
    /// <param name="line">Zero-based line number of cursor position</param>
    /// <param name="column">Zero-based column number of cursor position</param>
    /// <returns>Hover information if a symbol is found, otherwise null</returns>
    HoverInfo? GetHoverInfo(string code, int line, int column);

    /// <summary>
    /// Extracts all symbols from the source code for outline and navigation features.
    /// Performs a complete analysis to identify all named entities in the code.
    ///
    /// Extracted symbols include:
    /// - Function and routine declarations
    /// - Entity and struct definitions
    /// - Variable declarations
    /// - Type definitions and aliases
    /// - Enum declarations
    ///
    /// Used by IDEs for:
    /// - Document outline/symbol tree view
    /// - Quick symbol navigation
    /// - Find all symbols commands
    /// - Code folding and structure visualization
    /// </summary>
    /// <param name="code">Source code to extract symbols from</param>
    /// <returns>List of all symbols found in the code</returns>
    List<Symbol> GetSymbols(string code);
}

/// <summary>
/// Represents a single completion suggestion for autocomplete functionality.
/// Contains all information needed to display and insert a completion item in the editor.
///
/// Completion suggestions are generated based on:
/// - Current parsing context and expected tokens
/// - Available symbols in the current scope
/// - Language keywords and built-in functions
/// - User-defined types and functions
///
/// The LSP client uses this information to display completion popups
/// with appropriate icons, documentation, and insertion behavior.
/// </summary>
public record CompletionSuggestion
{
    /// <summary>
    /// The display name shown in the completion list.
    /// Should be the primary identifier that users will recognize.
    /// </summary>
    public string Label { get; init; } = "";

    /// <summary>
    /// Additional detail information shown alongside the label.
    /// Typically contains type information or brief descriptions.
    /// </summary>
    public string Detail { get; init; } = "";

    /// <summary>
    /// Extended documentation shown when the completion item is selected.
    /// Can include usage examples, parameter descriptions, and detailed explanations.
    /// </summary>
    public string Documentation { get; init; } = "";

    /// <summary>
    /// Classification of the completion item for icon selection and filtering.
    /// Determines how the IDE displays and categorizes the suggestion.
    /// </summary>
    public CompletionKind Kind { get; init; }

    /// <summary>
    /// Text to insert when the completion is accepted.
    /// May include placeholder variables for snippet expansion.
    /// </summary>
    public string InsertText { get; init; } = "";

    /// <summary>
    /// Indicates whether InsertText contains snippet syntax with placeholders.
    /// When true, the editor will enable tab-through navigation of placeholders.
    /// </summary>
    public bool IsSnippet { get; init; }
}

/// <summary>
/// Contains information displayed when hovering over a symbol in the editor.
/// Provides contextual documentation and type information for user-defined
/// and built-in language constructs.
///
/// Hover information enhances the development experience by providing
/// immediate access to symbol documentation without requiring navigation
/// to definition sites or external documentation.
/// </summary>
public record HoverInfo
{
    /// <summary>
    /// Main content to display in the hover popup.
    /// Typically formatted as Markdown for rich text presentation.
    /// </summary>
    public string Content { get; init; } = "";

    /// <summary>
    /// Type signature or classification of the symbol.
    /// Provides quick type information for variables, functions, and other entities.
    /// </summary>
    public string Type { get; init; } = "";

    /// <summary>
    /// Extended documentation for the symbol.
    /// Includes detailed descriptions, usage notes, and examples.
    /// </summary>
    public string Documentation { get; init; } = "";

    /// <summary>
    /// Source location of the symbol definition.
    /// Used for highlighting the relevant range in the editor.
    /// </summary>
    public SourceLocation Location { get; init; } = new(Line: 0, Column: 0, Position: 0);
}

/// <summary>
/// Enumeration of completion item types for categorization and icon selection.
/// Maps to LSP CompletionItemKind for consistent IDE presentation.
/// </summary>
public enum CompletionKind
{
    /// <summary>Plain text completion.</summary>
    Text,

    /// <summary>Entity method.</summary>
    Method,

    /// <summary>Standalone function or routine.</summary>
    Function,

    /// <summary>Constructor for classes or memory slices.</summary>
    Constructor,

    /// <summary>Entity or struct field.</summary>
    Field,

    /// <summary>Variable declaration (let, var, const).</summary>
    Variable,

    /// <summary>Entity definition.</summary>
    Entity,

    /// <summary>Interface definition.</summary>
    Interface,

    /// <summary>Module or namespace.</summary>
    Module,

    /// <summary>Property with getter/setter.</summary>
    Property,

    /// <summary>Unit of measurement or void type.</summary>
    Unit,

    /// <summary>Literal value or constant.</summary>
    Value,

    /// <summary>Enumeration type.</summary>
    Enum,

    /// <summary>Language keyword.</summary>
    Keyword,

    /// <summary>Code snippet with placeholders.</summary>
    Snippet,

    /// <summary>Color value.</summary>
    Color,

    /// <summary>File reference.</summary>
    File,

    /// <summary>Cross-reference to another symbol.</summary>
    Reference,

    /// <summary>Folder or directory.</summary>
    Folder,

    /// <summary>Member of an enumeration.</summary>
    EnumMember,

    /// <summary>Constant value or definition.</summary>
    Constant,

    /// <summary>Record definition.</summary>
    Record,

    /// <summary>Event handler or callback.</summary>
    Event,

    /// <summary>Operator symbol (+, -, *, etc.).</summary>
    Operator,

    /// <summary>Generic type parameter.</summary>
    TypeParameter
}
