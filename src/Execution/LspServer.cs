using System.Text;
using System.Text.Json;
using Compiler.Diagnostics;
using Compiler.Parser;
using Compiler.Declaration;
using Compiler.Tokenizer;
using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;
using Compiler.Verification;
using Compiler.Verification.Results;

namespace Builder;

/// <summary>
/// A Language Server Protocol server for RazorForge / Suflae, spoken over stdio (launched via
/// <c>RazorForge --lsp</c>). On every document open/change it runs the real tokenizer → parser →
/// semantic analyzer and reports positioned diagnostics; the analyzed AST + tokens are then reused
/// to serve hover (type / routine signature / variable binding), go-to-definition (routines
/// cross-file, variables/parameters scope-precise), references and rename (binding-precise via the
/// stamped <see cref="IdentifierExpression.ResolvedVariable"/>), completion (members after <c>.</c>,
/// else keywords / free routines / file declarations) with resolve, signature help, and semantic
/// tokens. Analysis reuses a pre-analyzed stdlib snapshot (captured once per language), so each
/// keystroke re-analyzes only the user file in microseconds rather than reloading the whole stdlib —
/// the same in-RAM-stdlib model the fast-rebuild dev loop needs.
///
/// Framing is LSP's <c>Content-Length</c>-delimited JSON-RPC 2.0. stdout carries ONLY protocol
/// bytes; every other write (the pipeline's own Console output) is redirected to stderr so it
/// cannot corrupt the channel.
/// </summary>
public static class LspServer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition =
            System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    // Repeated JSON property name constants (avoids repeated string literals flagged by S1192).
    private const string PropParams = "params";
    private const string PropTextDocument = "textDocument";
    private const string PropMarkdown = "markdown";
    private const string PropValue = "value";
    private const string PropRange = "range";
    private const string PropStart = "start";
    private const string PropCharacter = "character";
    private const string PropLabel = "label";
    private const string PropDocumentation = "documentation";
    private const string SuffixDeclaration = "Declaration";
    private const string NodeRoutineDeclaration = "RoutineDeclaration";

    // One pre-analyzed stdlib snapshot per language, captured on first use.
    private static readonly Lazy<TypeRegistry.StdlibSnapshot> RfSnapshot =
        new(valueFactory: () =>
            SemanticVerifier.CaptureStdlibSnapshot(language: Language.RazorForge));

    private static readonly Lazy<TypeRegistry.StdlibSnapshot> SfSnapshot =
        new(valueFactory: () => SemanticVerifier.CaptureStdlibSnapshot(language: Language.Suflae));

    /// <summary>The last analyzed state of an open document, kept so hover/definition/completion reuse it.</summary>
    private sealed record DocState(
        SyntaxTree.Program Program,
        List<Token> Tokens,
        Language Lang,
        TypeRegistry Registry);

    // Open documents by URI: their most recent typed AST + token stream (for hover).
    private static readonly Dictionary<string, DocState> Docs = new();

    /// <summary>
    /// Runs the LSP server, reading JSON-RPC 2.0 messages from stdin and writing responses to
    /// stdout. Redirects all pipeline <c>Console.Write</c> output to stderr so it cannot corrupt
    /// the LSP framing. Returns 0 on a clean shutdown (client sent <c>shutdown</c> then
    /// <c>exit</c>), or 1 if <c>exit</c> arrives without a prior <c>shutdown</c>.
    /// </summary>
    public static int Run()
    {
        // The protocol owns the real stdout; send stray pipeline output to stderr instead.
        Stream stdout = Console.OpenStandardOutput();
        Stream stdin = Console.OpenStandardInput();
        Console.SetOut(newOut: Console.Error);
        return Run(stdin: stdin, stdout: stdout);
    }

    /// <summary>
    /// Core JSON-RPC loop, parameterized over the transport streams so tests can drive the full
    /// request/response surface in-process by feeding LSP-framed messages through a
    /// <see cref="MemoryStream"/> and reading the framed replies back. The public
    /// <see cref="Run()"/> binds these to the process stdin/stdout.
    /// </summary>
    internal static int Run(Stream stdin, Stream stdout)
    {
        // Fresh transport = fresh document set; a real server starts with none open, and clearing
        // here keeps successive in-process test sessions from leaking state into one another.
        Docs.Clear();

        bool shutdownRequested = false;
        while (true)
        {
            byte[]? body = ReadMessage(stdin: stdin);
            if (body == null)
            {
                break; // EOF
            }

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(utf8Json: body);
            }
            catch (JsonException)
            {
                continue; // malformed frame — skip
            }

            using (doc)
            {
                JsonElement root = doc.RootElement;
                string method =
                    root.TryGetProperty(propertyName: "method", value: out JsonElement m)
                        ? m.GetString() ?? ""
                        : "";
                bool hasId = root.TryGetProperty(propertyName: "id", value: out JsonElement id);

                switch (method)
                {
                    case "initialize":
                        WriteResult(stdout: stdout,
                            id: id,
                            result: new Dictionary<string, object?>
                            {
                                [key: "capabilities"] = new Dictionary<string, object?>
                                {
                                    // 1 = Full document sync: didChange carries the whole text.
                                    [key: "textDocumentSync"] =
                                        new Dictionary<string, object?>
                                        {
                                            [key: "openClose"] = true, [key: "change"] = 1
                                        },
                                    [key: "hoverProvider"] = true,
                                    [key: "definitionProvider"] = true,
                                    [key: "referencesProvider"] = true,
                                    [key: "renameProvider"] =
                                        new Dictionary<string, object?>
                                        {
                                            [key: "prepareProvider"] = true
                                        },
                                    [key: "completionProvider"] =
                                        new Dictionary<string, object?>
                                        {
                                            [key: "triggerCharacters"] = new List<object?> { "." },
                                            [key: "resolveProvider"] = true
                                        },
                                    [key: "signatureHelpProvider"] =
                                        new Dictionary<string, object?>
                                        {
                                            [key: "triggerCharacters"] =
                                                new List<object?> { "(", "," }
                                        },
                                    [key: "documentSymbolProvider"] = true,
                                    [key: "workspaceSymbolProvider"] = true,
                                    [key: "inlayHintProvider"] = true,
                                    [key: "codeActionProvider"] = true,
                                    [key: "semanticTokensProvider"] =
                                        new Dictionary<string, object?>
                                        {
                                            [key: "legend"] = new Dictionary<string, object?>
                                            {
                                                [key: "tokenTypes"] = SemanticTokenTypes,
                                                [key: "tokenModifiers"] = SemanticTokenModifiers
                                            },
                                            [key: "full"] = true
                                        }
                                },
                                [key: "serverInfo"] = new Dictionary<string, object?>
                                {
                                    [key: "name"] = "razorforge-lsp", [key: "version"] = "0.1"
                                }
                            });
                        break;

                    case "shutdown":
                        shutdownRequested = true;
                        WriteResult(stdout: stdout, id: id, result: null);
                        break;

                    case "exit":
                        return shutdownRequested
                            ? 0
                            : 1;

                    case "textDocument/didOpen":
                        HandleDidOpenOrChange(stdout: stdout, root: root, isOpen: true);
                        break;

                    case "textDocument/didChange":
                        HandleDidOpenOrChange(stdout: stdout, root: root, isOpen: false);
                        break;

                    case "textDocument/didClose":
                        HandleDidClose(stdout: stdout, root: root);
                        break;

                    case "textDocument/hover":
                        HandleHover(stdout: stdout, id: id, root: root);
                        break;

                    case "textDocument/definition":
                        HandleDefinition(stdout: stdout, id: id, root: root);
                        break;

                    case "textDocument/references":
                        HandleReferences(stdout: stdout, id: id, root: root);
                        break;

                    case "textDocument/completion":
                        HandleCompletion(stdout: stdout, id: id, root: root);
                        break;

                    case "completionItem/resolve":
                        HandleCompletionResolve(stdout: stdout, id: id, root: root);
                        break;

                    case "textDocument/signatureHelp":
                        HandleSignatureHelp(stdout: stdout, id: id, root: root);
                        break;

                    case "textDocument/prepareRename":
                        HandlePrepareRename(stdout: stdout, id: id, root: root);
                        break;

                    case "textDocument/rename":
                        HandleRename(stdout: stdout, id: id, root: root);
                        break;

                    case "textDocument/documentSymbol":
                        HandleDocumentSymbol(stdout: stdout, id: id, root: root);
                        break;

                    case "workspace/symbol":
                        HandleWorkspaceSymbol(stdout: stdout, id: id, root: root);
                        break;

                    case "textDocument/inlayHint":
                        HandleInlayHint(stdout: stdout, id: id, root: root);
                        break;

                    case "textDocument/codeAction":
                        HandleCodeAction(stdout: stdout, id: id, root: root);
                        break;

                    case "textDocument/semanticTokens/full":
                        HandleSemanticTokens(stdout: stdout, id: id, root: root);
                        break;

                    default:
                        // Unknown REQUEST (has id) — answer with an empty result so the client
                        // does not stall. Unknown NOTIFICATIONS are simply ignored.
                        if (hasId)
                        {
                            WriteResult(stdout: stdout, id: id, result: null);
                        }

                        break;
                }
            }
        }

        return 0;
    }

    private static void HandleDidOpenOrChange(Stream stdout, JsonElement root, bool isOpen)
    {
        if (!root.TryGetProperty(propertyName: PropParams, value: out JsonElement p) ||
            !p.TryGetProperty(propertyName: PropTextDocument, value: out JsonElement td) ||
            !td.TryGetProperty(propertyName: "uri", value: out JsonElement uriEl))
        {
            return;
        }

        string uri = uriEl.GetString() ?? "";
        string? text;
        if (isOpen)
        {
            text = td.TryGetProperty(propertyName: "text", value: out JsonElement t)
                ? t.GetString()
                : null;
        }
        else
        {
            text = ExtractFullChangeText(paramsEl: p);
        }

        if (text == null)
        {
            return;
        }

        List<Dictionary<string, object?>> diagnostics = Analyze(uri: uri, text: text);
        PublishDiagnostics(stdout: stdout, uri: uri, diagnostics: diagnostics);
    }

    private static void HandleDidClose(Stream stdout, JsonElement root)
    {
        if (root.TryGetProperty(propertyName: PropParams, value: out JsonElement p) &&
            p.TryGetProperty(propertyName: PropTextDocument, value: out JsonElement td) &&
            td.TryGetProperty(propertyName: "uri", value: out JsonElement uriEl))
        {
            string uri = uriEl.GetString() ?? "";
            Docs.Remove(key: uri);
            PublishDiagnostics(stdout: stdout,
                uri: uri,
                diagnostics: new List<Dictionary<string, object?>>());
        }
    }

    /// <summary>
    /// <c>textDocument/hover</c>: report the resolved type of the expression under the cursor. Reuses
    /// the last analysis of the document (no re-parse) — finds the token at the position, then the
    /// typed expression anchored at it, and renders its type as a RazorForge code block.
    /// </summary>
    private static void HandleHover(Stream stdout, JsonElement id, JsonElement root)
    {
        if (!TryReadPosition(root: root,
                uri: out string uri,
                line0: out int line0,
                char0: out int char0) || !Docs.TryGetValue(key: uri, value: out DocState? doc))
        {
            WriteResult(stdout: stdout, id: id, result: null);
            return;
        }

        Token? hit = TokenAt(doc: doc, line0: line0, char0: char0);
        if (hit == null)
        {
            WriteResult(stdout: stdout, id: id, result: null);
            return;
        }

        Expression? best = BestTypedExpression(program: doc.Program,
            line1: line0 + 1,
            col1: char0 + 1,
            hit: hit);

        // Prefer a richer label when the token names a known kind of symbol:
        //   • a routine call/reference  → full signature `name(a: T, b: U) -> R`
        //   • a bound variable/parameter → `name: Type` (with a `(parameter)` note)
        //   • otherwise                  → the expression's resolved type
        string? label = null;
        string? documentation = null;
        if (IsIdentifierText(text: hit.Text))
        {
            (label, documentation) = SymbolHoverLabel(doc: doc, hit: hit);
        }

        if (label == null)
        {
            string? resolved = ResolveHoverFallbackLabel(doc: doc, hit: hit, best: best);
            if (resolved == null)
            {
                WriteResult(stdout: stdout, id: id, result: null);
                return;
            }

            label = resolved;
        }

        int endCol0 = hit.Column - 1 + hit.Text.Length;
        string hoverValue = $"```razorforge\n{label}\n```";
        if (!string.IsNullOrWhiteSpace(value: documentation))
        {
            hoverValue += $"\n\n{RenderDoc(doc: documentation)}";
        }

        WriteResult(stdout: stdout,
            id: id,
            result: new Dictionary<string, object?>
            {
                [key: "contents"] =
                    new Dictionary<string, object?>
                    {
                        [key: "kind"] = PropMarkdown, [key: PropValue] = hoverValue
                    },
                [key: PropRange] = new Dictionary<string, object?>
                {
                    [key: PropStart] =
                        new Dictionary<string, object?>
                        {
                            [key: "line"] = line0,
                            [key: PropCharacter] = hit.Column - 1
                        },
                    [key: "end"] = new Dictionary<string, object?>
                    {
                        [key: "line"] = line0, [key: PropCharacter] = endCol0
                    }
                }
            });
    }

    /// <summary>The best typed expression on the cursor line: the one anchored exactly at the hit token,
    /// or the closest-starting expression at or before the cursor column if no exact match.</summary>
    private static Expression? BestTypedExpression(SyntaxTree.Program program, int line1, int col1,
        Token hit)
    {
        var typed = AllNodes(program: program)
                   .OfType<Expression>()
                   .Where(predicate: e => e.ResolvedType != null && e.Location.Line == line1)
                   .ToList();

        Expression? best = null;
        foreach (Expression e in typed)
        {
            if (e.Location.Column == hit.Column)
            {
                return e; // exact anchor — the identifier/call at the cursor
            }

            if (e.Location.Column <= col1 &&
                (best == null || e.Location.Column > best.Location.Column))
            {
                best = e; // closest-starting enclosing expression as a fallback
            }
        }

        return best;
    }

    /// <summary>When no symbol-specific label was produced, falls back to the expression's resolved type.
    /// Returns null if the token is a realm qualifier (which has no value type) or if the best expression
    /// has no resolved type.</summary>
    private static string? ResolveHoverFallbackLabel(DocState doc, Token hit, Expression? best)
    {
        // A realm tag (`C`/`LLVM`/`RF`/`SF` before `::`) is a qualifier, not a value — don't report it
        // as the return type. If the qualified call resolved, the routine branch already handled it.
        if (IsRealmQualifier(doc: doc, hit: hit))
        {
            return null;
        }

        if (best?.ResolvedType == null)
        {
            return null;
        }

        string typeName = best.ResolvedType.Name;
        return IsIdentifierText(text: hit.Text)
            ? $"{hit.Text}: {typeName}"
            : typeName;
    }

    /// <summary>The richer hover label + documentation for an identifier token: a routine's signature, or a
    /// bound variable/parameter's <c>name: Type</c> plus ownership notes. Both are null when the token names
    /// neither (the caller then falls back to the resolved expression type).</summary>
    private static (string? Label, string? Documentation) SymbolHoverLabel(DocState doc, Token hit)
    {
        RoutineInfo? routine = RoutineReferencedByToken(doc: doc, hit: hit);
        if (routine != null)
        {
            return ($"routine {routine.Name}{RoutineDetail(r: routine)}", routine.Documentation);
        }

        VariableInfo? bound = VariableBoundAtToken(doc: doc, hit: hit);
        if (bound == null)
        {
            return (null, null);
        }

        string kindNote = bound.IsPreset
            ? "preset "
            : "";
        string label = $"{kindNote}{bound.Name}: {bound.Type.Name}";

        // Ownership state: is this exact occurrence dead (moved out by an earlier steal)?
        bool deadHere = AllNodes(program: doc.Program)
                       .OfType<IdentifierExpression>()
                       .Any(predicate: e => e.IsDeadUse && e.Name == hit.Text &&
                                            e.Location.Line == hit.Line &&
                                            e.Location.Column == hit.Column);
        var notes = new List<string>();
        if (deadHere)
        {
            notes.Add(item: "⚠️ **moved out** — this value's ownership was transferred by an " +
                            "earlier `steal`; it is dead here (use-after-steal) until re-assigned.");
        }

        if (OwnershipNote(type: bound.Type) is { } own)
        {
            notes.Add(item: own);
        }

        string? documentation = notes.Count > 0
            ? string.Join(separator: "\n\n", values: notes)
            : null;
        return (label, documentation);
    }

    /// <summary>
    /// If the identifier token names a routine CALL or reference, the resolved routine — matched through
    /// the analyzer's <c>ResolvedRoutine</c> so it works cross-file. A bare-identifier callee must sit
    /// exactly at the token; a member callee (<c>x.foo()</c>) matches by name + line.
    /// </summary>
    private static RoutineInfo? RoutineReferencedByToken(DocState doc, Token hit)
    {
        foreach (CallExpression call in AllNodes(program: doc.Program)
                    .OfType<CallExpression>())
        {
            if (call.ResolvedRoutine == null)
            {
                continue;
            }

            if (CalleeMatchesHit(callee: call.Callee, hit: hit))
            {
                return call.ResolvedRoutine;
            }
        }

        return null;
    }

    /// <summary>Whether a call's callee expression matches the hit token — used to resolve which routine
    /// a hovered/referenced identifier denotes. Handles both bare/realm-qualified identifiers and member
    /// call expressions.</summary>
    private static bool CalleeMatchesHit(Expression callee, Token hit)
    {
        switch (callee)
        {
            // A bare or realm-qualified callee (`foo` or `C::foo`). The IdentifierExpression is
            // anchored at its START — the realm tag when one is present — so `C::foo` spans the realm
            // token AND the name token. CheckAndAdvance EITHER, at its exact column, so hovering the `C` in
            // `C::rf_x()` resolves the routine instead of mistaking `C` for a value of the return type.
            case IdentifierExpression cid when cid.Location.Line == hit.Line:
            {
                int realmCol = cid.Location.Column;
                if (cid.Realm != null)
                {
                    int nameCol = realmCol + cid.Realm.Length + 2; // realm tag + "::"
                    return hit.Text == cid.Realm && hit.Column == realmCol ||
                           hit.Text == cid.Name && hit.Column == nameCol;
                }

                return hit.Text == cid.Name && hit.Column == realmCol;
            }

            // A member callee (`x.foo()`): match the member name by name + line.
            case MemberExpression m:
                return m.MemberName == hit.Text && m.Location.Line == hit.Line;

            default:
                return false;
        }
    }

    /// <summary>True when the token is a realm tag (<c>C</c>/<c>LLVM</c>/<c>RF</c>/<c>SF</c>) — i.e. the
    /// very next token on the line is the <c>::</c> separator. Such a token is a qualifier, not a value,
    /// so hover must not fall back to reporting it as a typed expression.</summary>
    private static bool IsRealmQualifier(DocState doc, Token hit)
    {
        int nextCol = hit.Column + hit.Text.Length;
        return doc.Tokens.Any(predicate: t => t.Type == TokenType.DoubleColon &&
                                              t.Line == hit.Line && t.Column == nextCol);
    }

    /// <summary>
    /// A one-line note on the ownership nature of a variable's type, for hover — so the ownership model is
    /// visible where it bites: an <c>entity</c> is single-owner (needs <c>steal</c> to hand off), a
    /// <c>Retained[T]</c> is a storable hand-off, the <c>Viewing</c>/<c>Modifying</c> tokens are temporary
    /// access links. Null for ordinary value types.
    /// </summary>
    private static string? OwnershipNote(TypeInfo type)
    {
        if (type is EntityTypeInfo)
        {
            return
                "🔒 **entity** — single owner. Hand it off with `steal` (a plain `=` is RF-S413); after " +
                "that the source binding is dead.";
        }

        return type.BareName switch
        {
            "Retained" => "📦 **Retained** — a persistent, storable ownership hand-off.",
            "Viewing" or "Modifying" =>
                "👁 **temporary access link** — not storable or returnable, valid only for this scope.",
            "Controlling" or "Accessing" => "🔗 a reference protocol, not a pass-currency.",
            _ => null
        };
    }

    /// <summary>The variable/parameter binding an identifier token resolved to (via the stamped
    /// <see cref="IdentifierExpression.ResolvedVariable"/>), or null.</summary>
    private static VariableInfo? VariableBoundAtToken(DocState doc, Token hit)
    {
        foreach (IdentifierExpression e in AllNodes(program: doc.Program)
                    .OfType<IdentifierExpression>())
        {
            if (e.ResolvedVariable != null && e.Name == hit.Text && e.Location.Line == hit.Line &&
                e.Location.Column == hit.Column)
            {
                return e.ResolvedVariable;
            }
        }

        return null;
    }

    /// <summary>
    /// <c>textDocument/definition</c>: jump to where the symbol under the cursor is defined. A routine
    /// call resolves through the analyzer's <c>ResolvedRoutine</c> — so it can jump CROSS-FILE, e.g. into
    /// the stdlib. Otherwise the name is matched against the declarations in this file (routine / type /
    /// variable). Parameters are not yet targets.
    /// </summary>
    private static void HandleDefinition(Stream stdout, JsonElement id, JsonElement root)
    {
        if (!TryReadPosition(root: root,
                uri: out string uri,
                line0: out int line0,
                char0: out int char0) || !Docs.TryGetValue(key: uri, value: out DocState? doc))
        {
            WriteResult(stdout: stdout, id: id, result: null);
            return;
        }

        Token? hit = TokenAt(doc: doc, line0: line0, char0: char0);
        if (hit == null || !IsIdentifierText(text: hit.Text))
        {
            WriteResult(stdout: stdout, id: id, result: null);
            return;
        }

        // 0. A variable / parameter use — jump to its binding site (scope-precise via ResolvedVariable).
        VariableInfo? bound = VariableBoundAtToken(doc: doc, hit: hit);
        if (bound?.Location is { } vloc)
        {
            WriteResult(stdout: stdout, id: id, result: LocationToLsp(loc: vloc));
            return;
        }

        List<ISyntaxTreeNode> nodes = AllNodes(program: doc.Program);

        // 1. A routine call — jump to the resolved routine's definition (cross-file capable).
        SourceLocation? routineLoc = FindCallDefinitionLocation(nodes: nodes, hit: hit);
        if (routineLoc != null)
        {
            WriteResult(stdout: stdout, id: id, result: LocationToLsp(loc: routineLoc));
            return;
        }

        // 2. A declaration in this file with the matching name (routine / type / variable). For variables,
        //    prefer the nearest declaration at or above the use.
        SyntaxTreeNode? bestDecl = FindBestDeclarationNode(nodes: nodes, hit: hit);
        WriteResult(stdout: stdout,
            id: id,
            result: bestDecl != null
                ? LocationToLsp(loc: bestDecl.Location)
                : null);
    }

    /// <summary>Finds the definition location of the resolved routine call whose callee matches the hit
    /// token, or null if none matches. Checks both exact-column match (bare identifier) and
    /// name+line match (member callee).</summary>
    private static SourceLocation? FindCallDefinitionLocation(List<ISyntaxTreeNode> nodes,
        Token hit)
    {
        foreach (CallExpression call in nodes.OfType<CallExpression>())
        {
            if (call.ResolvedRoutine?.Location is not { } rloc)
            {
                continue;
            }

            (string? cname, SourceLocation? cloc) = CalleeName(callee: call.Callee);
            if (cname != hit.Text || cloc == null || cloc.Line != hit.Line)
            {
                continue;
            }

            // A bare identifier callee must sit exactly at the token; a member callee matches by name + line.
            if (call.Callee is IdentifierExpression && cloc.Column != hit.Column)
            {
                continue;
            }

            return rloc;
        }

        return null;
    }

    /// <summary>Finds the declaration node in this file whose name matches the hit token. For variables,
    /// prefers the nearest declaration at or above the use site.</summary>
    private static SyntaxTreeNode? FindBestDeclarationNode(List<ISyntaxTreeNode> nodes, Token hit)
    {
        SyntaxTreeNode? bestDecl = null;
        foreach (ISyntaxTreeNode node in nodes)
        {
            if (node is not SyntaxTreeNode sn || !node.GetType()
                                                      .Name
                                                      .EndsWith(value: SuffixDeclaration,
                                                           comparisonType:
                                                           StringComparison.Ordinal) ||
                GetNameProp(node: node) != hit.Text)
            {
                continue;
            }

            if (bestDecl == null || sn.Location.Line <= hit.Line &&
                sn.Location.Line > bestDecl.Location.Line)
            {
                bestDecl = sn;
            }
        }

        return bestDecl;
    }

    // Semantic-tokens legend (order defines the indices sent in the delta stream). Indices are
    // referenced by name through SemTok(...) so the emitter can never drift out of sync with this list.
    private static readonly List<object?> SemanticTokenTypes = new()
    {
        "function", // 0
        "variable", // 1
        "parameter", // 2
        "type", // 3
        "property", // 4
        "keyword", // 5
        "string", // 6
        "number", // 7
        "comment" // 8
    };

    private static int SemTok(string name)
    {
        return SemanticTokenTypes.IndexOf(item: name);
    }

    // Token modifiers. `deprecated` (bit 0) marks a DEAD use — a variable read after its ownership was
    // moved out by `steal` — which editors render struck-through / faded (the ownership grey-out).
    private static readonly List<object?> SemanticTokenModifiers = new() { "deprecated" };
    private const int ModDeprecated = 1; // 1 << 0

    // Completion keyword set (shared RF/SF surface; RF-only ones are harmless in SF suggestions).
    private static readonly string[] Keywords =
    {
        "routine",
        "entity",
        "record",
        "choice",
        "variant",
        "protocol",
        "flags",
        "crashable",
        "var",
        "preset",
        "lateinit",
        "secret",
        "posted",
        "common",
        "me",
        "Me",
        "obeys",
        "disobeys",
        "needs",
        "relates",
        "everywhere",
        "if",
        "elseif",
        "else",
        "then",
        "unless",
        "when",
        "is",
        "isnot",
        "loop",
        "while",
        "each",
        "break",
        "continue",
        "return",
        "throw",
        "absent",
        "becomes",
        "pierce",
        "in",
        "notin",
        "to",
        "til",
        "by",
        "steal",
        "import",
        "module",
        "using",
        "as",
        "define",
        "pass",
        "with",
        "given",
        "discard",
        "and",
        "or",
        "not",
        "but",
        "true",
        "false",
        "None",
        "none",
        "suspended",
        "threaded",
        "danger",
        "dangerous",
        "global"
    };

    /// <summary>
    /// <c>textDocument/references</c>: all occurrences of the symbol under the cursor, in this file.
    /// A variable/parameter binds by <see cref="IdentifierExpression.ResolvedVariable"/> identity, so
    /// only the SAME binding is returned (a shadowing same-name local is excluded); a routine binds by
    /// its resolved identity. Types and unresolved names fall back to same-name identifier matching.
    /// </summary>
    private static void HandleReferences(Stream stdout, JsonElement id, JsonElement root)
    {
        if (!TryReadPosition(root: root,
                uri: out string uri,
                line0: out int line0,
                char0: out int char0) || !Docs.TryGetValue(key: uri, value: out DocState? doc))
        {
            WriteResult(stdout: stdout, id: id, result: null);
            return;
        }

        Token? hit = TokenAt(doc: doc, line0: line0, char0: char0);
        if (hit == null || !IsIdentifierText(text: hit.Text))
        {
            WriteResult(stdout: stdout, id: id, result: new List<object?>());
            return;
        }

        var locs = new List<object?>();
        foreach ((int line, int col, int len) in SymbolOccurrences(doc: doc, hit: hit))
        {
            locs.Add(item: RangeLsp(uri: uri,
                line1: line,
                col1: col,
                length: len));
        }

        WriteResult(stdout: stdout, id: id, result: locs);
    }

    /// <summary>
    /// The 1-based (line, column, length) spans of every occurrence of the symbol the cursor is on,
    /// within this document. Shared by references and rename. Resolution order:
    /// <list type="number">
    /// <item>a variable/parameter binding (reference-identity on <c>ResolvedVariable</c>, plus its
    /// declaration site), then</item>
    /// <item>a routine (identity by <c>RegistryKey</c> across call sites, plus a same-file definition),
    /// then</item>
    /// <item>a same-name identifier text match (types / unresolved names).</item>
    /// </list>
    /// </summary>
    private static List<(int Line, int Col, int Len)> SymbolOccurrences(DocState doc, Token hit)
    {
        int len = hit.Text.Length;
        var result = new List<(int, int, int)>();
        var seen = new HashSet<(int, int)>();

        void Add(int line, int col)
        {
            if (line > 0 && col > 0 && seen.Add(item: (line, col)))
            {
                result.Add(item: (line, col, len));
            }
        }

        List<ISyntaxTreeNode> nodes = AllNodes(program: doc.Program);
        var idents = nodes.OfType<IdentifierExpression>()
                          .ToList();

        // 1. Variable / parameter binding. The cursor may be on a use OR on the declaration name.
        VariableInfo? binding = FindVariableBinding(doc: doc, hit: hit, idents: idents);
        if (binding != null)
        {
            CollectVariableOccurrences(binding: binding, idents: idents, add: Add);
            return result;
        }

        // 2. Routine identity — same-file call sites + a same-file definition.
        RoutineInfo? routine = RoutineReferencedByToken(doc: doc, hit: hit) ??
                               RoutineDefinedAtToken(doc: doc, hit: hit, nodes: nodes);
        if (routine != null)
        {
            CollectRoutineOccurrences(routine: routine,
                hit: hit,
                nodes: nodes,
                add: Add);
            if (result.Count > 0)
            {
                return result;
            }
        }

        // 3. Fallback — same-name identifier tokens (types, unresolved names).
        foreach (Token t in doc.Tokens.Where(predicate: t =>
                     t.Type == TokenType.Identifier && t.Text == hit.Text))
        {
            Add(line: t.Line, col: t.Column);
        }

        return result;
    }

    /// <summary>Finds the variable/parameter binding that the hit token refers to. Checks the cursor token
    /// first (via <see cref="VariableBoundAtToken"/>), then scans all identifier expressions to find one
    /// whose binding declaration is at the cursor position (handles clicking the declaration site).</summary>
    private static VariableInfo? FindVariableBinding(DocState doc, Token hit,
        List<IdentifierExpression> idents)
    {
        VariableInfo? binding = VariableBoundAtToken(doc: doc, hit: hit);
        if (binding != null)
        {
            return binding;
        }

        return idents.Select(selector: e => e.ResolvedVariable)
                     .FirstOrDefault(predicate: v => v?.Location is { } l &&
                                                     l.Line == hit.Line && l.Column == hit.Column);
    }

    /// <summary>Adds all occurrence positions of a variable binding to the accumulator: every use site
    /// (via ResolvedVariable identity) and the declaration site itself.</summary>
    private static void CollectVariableOccurrences(VariableInfo binding,
        List<IdentifierExpression> idents, Action<int, int> add)
    {
        foreach (SourceLocation loc in idents
                                      .Where(predicate: e =>
                                           ReferenceEquals(objA: e.ResolvedVariable,
                                               objB: binding))
                                      .Select(selector: e => e.Location))
        {
            add(arg1: loc.Line, arg2: loc.Column);
        }

        if (binding.Location is { } decl)
        {
            add(arg1: decl.Line, arg2: decl.Column);
        }
    }

    /// <summary>Adds all occurrence positions of a routine (call sites + declaration) to the accumulator.</summary>
    private static void CollectRoutineOccurrences(RoutineInfo routine, Token hit,
        List<ISyntaxTreeNode> nodes, Action<int, int> add)
    {
        string key = routine.RegistryKey;
        foreach (CallExpression call in nodes.OfType<CallExpression>())
        {
            if (call.ResolvedRoutine?.RegistryKey != key)
            {
                continue;
            }

            (string? cn, SourceLocation? cl) = CalleeName(callee: call.Callee);
            if (cn == hit.Text && cl != null)
            {
                add(arg1: cl.Line, arg2: cl.Column);
            }
        }

        foreach (ISyntaxTreeNode node in nodes)
        {
            if (node is SyntaxTreeNode sn && node.GetType()
                                                 .Name == NodeRoutineDeclaration &&
                GetNameProp(node: node) == hit.Text)
            {
                add(arg1: sn.Location.Line, arg2: sn.Location.Column);
            }
        }
    }

    /// <summary>If the token sits on a routine's declaration name, that routine (matched by name).</summary>
    private static RoutineInfo? RoutineDefinedAtToken(DocState doc, Token hit,
        List<ISyntaxTreeNode> nodes)
    {
        bool onDecl = nodes.Any(predicate: n => n is SyntaxTreeNode sn && n.GetType()
                                                   .Name == NodeRoutineDeclaration &&
                                                GetNameProp(node: n) == hit.Text &&
                                                sn.Location.Line == hit.Line);
        if (!onDecl)
        {
            return null;
        }

        return doc.Registry
                  .GetAllRoutines()
                  .FirstOrDefault(predicate: r => r.Name == hit.Text && r.Location is { } l &&
                                                  l.Line == hit.Line);
    }

    /// <summary>
    /// <c>textDocument/completion</c>: after a <c>.</c>, the receiver type's members; otherwise keywords,
    /// visible free routines, and this file's declarations.
    /// </summary>
    private static void HandleCompletion(Stream stdout, JsonElement id, JsonElement root)
    {
        if (!TryReadPosition(root: root,
                uri: out string uri,
                line0: out int line0,
                char0: out int char0) || !Docs.TryGetValue(key: uri, value: out DocState? doc))
        {
            WriteResult(stdout: stdout,
                id: id,
                result: new Dictionary<string, object?>
                {
                    [key: "isIncomplete"] = false, [key: "items"] = new List<object?>()
                });
            return;
        }

        var items = new List<Dictionary<string, object?>>();
        var seen = new HashSet<string>();
        PopulateCompletionItems(doc: doc,
            line0: line0,
            char0: char0,
            items: items,
            seen: seen);

        WriteResult(stdout: stdout,
            id: id,
            result: new Dictionary<string, object?>
            {
                [key: "isIncomplete"] = false, [key: "items"] = items
            });
    }

    /// <summary>Fills the completion item list based on context: realm qualifier → realm-only routines;
    /// member dot → receiver type members; otherwise → global keywords / routines / declarations.</summary>
    private static void PopulateCompletionItems(DocState doc, int line0, int char0,
        List<Dictionary<string, object?>> items, HashSet<string> seen)
    {
        // After a `Realm::` (C / LLVM / RF / SF) list ONLY that realm's routines.
        string? realm = RealmQualifierBefore(doc: doc, line0: line0, char0: char0);
        if (realm != null)
        {
            AddRealmCompletions(doc: doc,
                items: items,
                seen: seen,
                realm: realm);
            return;
        }

        // After a `receiver.` commit to members only — if the receiver type can't be resolved,
        // return an empty list rather than spilling globals.
        Token? receiver = MemberReceiverToken(doc: doc, line0: line0, char0: char0);
        if (receiver != null)
        {
            TypeInfo? receiverType = ReceiverType(doc: doc, receiver: receiver);
            if (receiverType != null)
            {
                AddMemberCompletions(doc: doc,
                    items: items,
                    seen: seen,
                    receiver: receiver,
                    receiverType: receiverType);
            }

            return;
        }

        AddGlobalCompletions(doc: doc, items: items, seen: seen);
    }

    /// <summary>Completions after a <c>Realm::</c> qualifier: only that realm's free routines.</summary>
    private static void AddRealmCompletions(DocState doc, List<Dictionary<string, object?>> items,
        HashSet<string> seen, string realm)
    {
        RoutineRealm? want = realm switch
        {
            "C" => RoutineRealm.C,
            "LLVM" => RoutineRealm.LLVM,
            "RF" => RoutineRealm.RF,
            "SF" => RoutineRealm.SF,
            _ => null
        };
        if (want is not { } wr)
        {
            return;
        }

        foreach (RoutineInfo r in doc.Registry.GetAllRoutines())
        {
            if (r.Realm == wr && r.OwnerType == null && !r.Name.StartsWith(value: '$'))
            {
                AddItem(items: items,
                    seen: seen,
                    label: r.Name,
                    kind: 3,
                    detail: RoutineDetail(r: r),
                    documentation: r.Documentation);
            }
        }
    }

    /// <summary>Completions after a <c>receiver.</c>: the receiver type's member variables and applicable
    /// member routines (wired internals, file-private secrets, and specialized-receiver mismatches filtered).</summary>
    private static void AddMemberCompletions(DocState doc, List<Dictionary<string, object?>> items,
        HashSet<string> seen, Token receiver, TypeInfo receiverType)
    {
        // `secret` members are file-private — hide them from an outside `x.` completion, but show
        // them for `me.` (inside the type's own body they are accessible).
        bool includeSecret = receiver.Text == "me";

        foreach ((string name, string type) in MemberVariableSignatures(type: receiverType,
                     includeSecret: includeSecret))
        {
            AddItem(items: items,
                seen: seen,
                label: name,
                kind: 5,
                detail: $": {type}"); // Field
        }

        // Resolved own member routines — GetOwnMemberRoutinesResolved substitutes the generic
        // definition's methods for a concrete instantiation (so `List[FaceDraw].` shows `add_last`,
        // which the raw GetMemberRoutinesForType misses because methods register under `List[T]`).
        var ownMethods = doc.Registry
                            .GetOwnMemberRoutinesResolved(type: receiverType)
                            .ToList();

        // Methods whose SPECIALIZED receiver doesn't accept this instantiation (e.g.
        // `List[Agent[V]].gather` on a `List[FaceDraw]`). The compiler-generated failable variants
        // (`try_`/`check_`/`lookup_gather`) carry no MeType, so key the rejection on the BASE name
        // and let a variant inherit its base's (in)applicability.
        var rejected = new HashSet<string>(collection: ownMethods
                                                      .Where(predicate: mr =>
                                                           !ReceiverAcceptsMethod(mr: mr,
                                                               receiverType: receiverType))
                                                      .Select(selector: mr => mr.Name),
            comparer: StringComparer.Ordinal);

        foreach (RoutineInfo mr in ownMethods.Where(predicate: mr =>
                     !mr.Name.StartsWith(value: '$') &&
                     (includeSecret || mr.Visibility != VisibilityModifier.Secret) &&
                     !IsMethodRejected(name: mr.Name, rejected: rejected)))
        {
            AddItem(items: items,
                seen: seen,
                label: mr.Name,
                kind: 2, // Method
                detail: RoutineDetail(r: mr),
                documentation: mr.Documentation);
        }
    }

    /// <summary>Returns true when the method name is in the rejected set directly, or is a compiler-generated
    /// failable variant (<c>try_</c>/<c>check_</c>/<c>lookup_</c>) whose base name is rejected.</summary>
    private static readonly string[] MethodLookupPrefixes = ["try_", "check_", "lookup_"];

    private static bool IsMethodRejected(string name, HashSet<string> rejected)
    {
        if (rejected.Contains(item: name))
        {
            return true;
        }

        return MethodLookupPrefixes.Any(predicate: pfx =>
            name.StartsWith(value: pfx, comparisonType: StringComparison.Ordinal) &&
            rejected.Contains(item: name[pfx.Length..]));
    }

    /// <summary>Global (non-member) completions: keywords, visible free routines, and this file's
    /// top-level declarations.</summary>
    private static void AddGlobalCompletions(DocState doc, List<Dictionary<string, object?>> items,
        HashSet<string> seen)
    {
        foreach (string kw in Keywords)
        {
            AddItem(items: items,
                seen: seen,
                label: kw,
                kind: 14); // Keyword
        }

        foreach (RoutineInfo r in doc.Registry.GetAllRoutines())
        {
            if (r.OwnerType == null && !r.Name.StartsWith(value: '$'))
            {
                AddItem(items: items,
                    seen: seen,
                    label: r.Name,
                    kind: 3, // FreeRoutine
                    detail: RoutineDetail(r: r),
                    documentation: r.Documentation);
            }
        }

        foreach (ISyntaxTreeNode node in AllNodes(program: doc.Program))
        {
            string tn = node.GetType()
                            .Name;
            if (tn.EndsWith(value: SuffixDeclaration, comparisonType: StringComparison.Ordinal) &&
                GetNameProp(node: node) is { } dn)
            {
                int kind = tn switch // Var=6, Func=3, Class/type=7
                {
                    "VariableDeclaration" => 6,
                    NodeRoutineDeclaration => 3,
                    _ => 7
                };
                AddItem(items: items,
                    seen: seen,
                    label: dn,
                    kind: kind);
            }
        }
    }

    /// <summary>
    /// <c>completionItem/resolve</c>: promote the item's signature <c>detail</c> into a rendered
    /// markdown documentation panel. (There is no stored docstring to attach yet.)
    /// </summary>
    private static void HandleCompletionResolve(Stream stdout, JsonElement id, JsonElement root)
    {
        if (!root.TryGetProperty(propertyName: PropParams, value: out JsonElement item))
        {
            WriteResult(stdout: stdout, id: id, result: null);
            return;
        }

        var resolved = new Dictionary<string, object?>();
        string? label = null;
        string? detail = null;
        if (item.TryGetProperty(propertyName: PropLabel, value: out JsonElement lbl))
        {
            label = lbl.GetString();
            resolved[key: PropLabel] = label;
        }

        if (item.TryGetProperty(propertyName: "kind", value: out JsonElement k) &&
            k.ValueKind == JsonValueKind.Number)
        {
            resolved[key: "kind"] = k.GetInt32();
        }

        if (item.TryGetProperty(propertyName: "detail", value: out JsonElement d))
        {
            detail = d.GetString();
            resolved[key: "detail"] = detail;
        }

        if (item.TryGetProperty(propertyName: "insertText", value: out JsonElement it))
        {
            resolved[key: "insertText"] = it.GetString();
        }

        // Keep a real doc-comment if completion already attached one; otherwise promote the signature
        // detail into a rendered panel so at least the type shows.
        if (item.TryGetProperty(propertyName: PropDocumentation,
                value: out JsonElement existingDoc))
        {
            string? docValue;
            if (existingDoc.ValueKind == JsonValueKind.String)
            {
                docValue = existingDoc.GetString();
            }
            else if (existingDoc.TryGetProperty(propertyName: PropValue,
                         value: out JsonElement dv))
            {
                docValue = dv.GetString();
            }
            else
            {
                docValue = null;
            }

            resolved[key: PropDocumentation] = new Dictionary<string, object?>
            {
                [key: "kind"] = PropMarkdown, [key: PropValue] = docValue ?? ""
            };
        }
        else if (!string.IsNullOrEmpty(value: detail))
        {
            resolved[key: PropDocumentation] = new Dictionary<string, object?>
            {
                [key: "kind"] = PropMarkdown,
                [key: PropValue] = $"```razorforge\n{label}{detail}\n```"
            };
        }

        WriteResult(stdout: stdout, id: id, result: resolved);
    }

    /// <summary>
    /// <c>textDocument/signatureHelp</c>: while the cursor is inside a call's argument list, show the
    /// callee's signature and highlight the active parameter. The enclosing call and the active-argument
    /// index are found from the token stream (balanced parens + comma count), then the callee name is
    /// resolved to a routine through the analyzed AST (falling back to a same-name free routine).
    /// </summary>
    /// <summary>Scans the tokens before the cursor (balanced parens + comma count) to find the call whose
    /// argument list the cursor is inside: its callee name, the callee's line, and the active-argument
    /// index. Returns null when the cursor is not inside any call's argument list.</summary>
    private static (string? Callee, int Line, int Commas)? EnclosingCall(DocState doc, int line0,
        int char0)
    {
        int line1 = line0 + 1;
        int col1 = char0 + 1;
        var pre = doc.Tokens
                     .Where(predicate: t =>
                          t.Type != TokenType.Newline && t.Type != TokenType.Eof &&
                          t.Text.Length > 0 &&
                          (t.Line < line1 || t.Line == line1 && t.Column < col1))
                     .OrderBy(keySelector: t => t.Line)
                     .ThenBy(keySelector: t => t.Column)
                     .ToList();

        var stack = new Stack<(string? Callee, int Line, int Commas)>();
        Token? prev = null;
        foreach (Token t in pre)
        {
            switch (t.Text)
            {
                case "(":
                    stack.Push(item: (prev is { Type: TokenType.Identifier }
                        ? prev.Text
                        : null, prev?.Line ?? t.Line, 0));
                    break;
                case ")" when stack.Count > 0:
                    stack.Pop();
                    break;
                case "," when stack.Count > 0:
                    (string? Callee, int Line, int Commas) top = stack.Pop();
                    stack.Push(item: (top.Callee, top.Line, top.Commas + 1));
                    break;
            }

            prev = t;
        }

        return stack.Count > 0
            ? stack.Peek()
            : null;
    }

    private static void HandleSignatureHelp(Stream stdout, JsonElement id, JsonElement root)
    {
        if (!TryReadPosition(root: root,
                uri: out string uri,
                line0: out int line0,
                char0: out int char0) || !Docs.TryGetValue(key: uri, value: out DocState? doc))
        {
            WriteResult(stdout: stdout, id: id, result: null);
            return;
        }

        if (EnclosingCall(doc: doc, line0: line0, char0: char0) is not
            { Callee: not null } enclosing)
        {
            WriteResult(stdout: stdout, id: id, result: null);
            return;
        }

        (string? calleeName, int calleeLine, int activeParam) = enclosing;
        RoutineInfo? routine =
            ResolveSignatureRoutine(doc: doc, calleeName: calleeName, calleeLine: calleeLine);
        if (routine == null)
        {
            WriteResult(stdout: stdout, id: id, result: null);
            return;
        }

        WriteResult(stdout: stdout,
            id: id,
            result: BuildSignatureHelpResult(routine: routine, activeParam: activeParam));
    }

    /// <summary>Resolves the routine named by the callee at the given line, first from resolved call
    /// expressions (preferring the exact line), then from free routines in the registry.</summary>
    private static RoutineInfo? ResolveSignatureRoutine(DocState doc, string calleeName,
        int calleeLine)
    {
        RoutineInfo? routine = null;
        foreach (CallExpression call in AllNodes(program: doc.Program)
                    .OfType<CallExpression>())
        {
            if (call.ResolvedRoutine == null)
            {
                continue;
            }

            (string? cn, SourceLocation? cl) = CalleeName(callee: call.Callee);
            if (cn == calleeName)
            {
                routine = call.ResolvedRoutine;
                if (cl?.Line == calleeLine)
                {
                    return routine; // exact call at this line — best match
                }
            }
        }

        return routine ?? doc.Registry
                             .GetAllRoutines()
                             .FirstOrDefault(predicate: r =>
                                  r.Name == calleeName && r.OwnerType == null);
    }

    /// <summary>Builds the LSP <c>SignatureHelp</c> response object for a resolved routine.</summary>
    private static Dictionary<string, object?> BuildSignatureHelpResult(RoutineInfo routine,
        int activeParam)
    {
        // Pull per-parameter descriptions from the routine's `:param name:` doc fields.
        DocInfo? sigDoc = string.IsNullOrWhiteSpace(value: routine.Documentation)
            ? null
            : ParseDoc(doc: routine.Documentation);

        List<object?> parameters = BuildSignatureParameters(routine: routine, sigDoc: sigDoc);

        int active = routine.Parameters.Count == 0
            ? 0
            : Math.Min(val1: activeParam, val2: routine.Parameters.Count - 1);

        object? docEntry = string.IsNullOrWhiteSpace(value: sigDoc?.Summary)
            ? null
            : (object?)new Dictionary<string, object?>
            {
                [key: "kind"] = PropMarkdown, [key: PropValue] = sigDoc.Summary
            };

        return new Dictionary<string, object?>
        {
            [key: "signatures"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    [key: PropLabel] = $"{routine.Name}{RoutineDetail(r: routine)}",
                    [key: "parameters"] = parameters,
                    // Signature-level doc = the SUMMARY only; per-parameter `:param:` text is attached to
                    // each parameter above, and Returns/Throws render in hover, so don't repeat them here.
                    [key: PropDocumentation] = docEntry
                }
            },
            [key: "activeSignature"] = 0,
            [key: "activeParameter"] = active
        };
    }

    /// <summary>Builds the parameter array for a signature help response, annotating each parameter
    /// with its doc-comment description when available.</summary>
    private static List<object?> BuildSignatureParameters(RoutineInfo routine, DocInfo? sigDoc)
    {
        return routine.Parameters
                      .Select(selector: p =>
                       {
                           var pdict = new Dictionary<string, object?>
                           {
                               [key: PropLabel] = $"{p.Name}: {p.Type.Name}"
                           };
                           string? pdesc = sigDoc
                                         ?.Params.FirstOrDefault(predicate: x => x.Name == p.Name)
                                          .Desc;
                           if (!string.IsNullOrWhiteSpace(value: pdesc))
                           {
                               pdict[key: PropDocumentation] = pdesc;
                           }

                           return (object?)pdict;
                       })
                      .ToList();
    }

    /// <summary><c>textDocument/prepareRename</c>: the identifier span under the cursor, or null.</summary>
    private static void HandlePrepareRename(Stream stdout, JsonElement id, JsonElement root)
    {
        if (!TryReadPosition(root: root,
                uri: out string uri,
                line0: out int line0,
                char0: out int char0) || !Docs.TryGetValue(key: uri, value: out DocState? doc))
        {
            WriteResult(stdout: stdout, id: id, result: null);
            return;
        }

        Token? hit = TokenAt(doc: doc, line0: line0, char0: char0);
        if (hit == null || !IsIdentifierText(text: hit.Text))
        {
            WriteResult(stdout: stdout, id: id, result: null);
            return;
        }

        int c = Math.Max(val1: 0, val2: hit.Column - 1);
        WriteResult(stdout: stdout,
            id: id,
            result: new Dictionary<string, object?>
            {
                [key: PropStart] =
                    new Dictionary<string, object?>
                    {
                        [key: "line"] = hit.Line - 1, [key: PropCharacter] = c
                    },
                [key: "end"] = new Dictionary<string, object?>
                {
                    [key: "line"] = hit.Line - 1,
                    [key: PropCharacter] = c + hit.Text.Length
                }
            });
    }

    /// <summary>
    /// <c>textDocument/rename</c>: rewrite every occurrence of the symbol under the cursor (same-file,
    /// binding-precise via <see cref="SymbolOccurrences"/>) to the new name, as a single WorkspaceEdit.
    /// </summary>
    private static void HandleRename(Stream stdout, JsonElement id, JsonElement root)
    {
        if (!TryReadPosition(root: root,
                uri: out string uri,
                line0: out int line0,
                char0: out int char0) || !Docs.TryGetValue(key: uri, value: out DocState? doc) ||
            !root.TryGetProperty(propertyName: PropParams, value: out JsonElement p) ||
            !p.TryGetProperty(propertyName: "newName", value: out JsonElement nn))
        {
            WriteResult(stdout: stdout, id: id, result: null);
            return;
        }

        string newName = nn.GetString() ?? "";
        Token? hit = TokenAt(doc: doc, line0: line0, char0: char0);
        if (hit == null || !IsIdentifierText(text: hit.Text) || newName.Length == 0)
        {
            WriteResult(stdout: stdout, id: id, result: null);
            return;
        }

        var edits = new List<object?>();
        foreach ((int line, int col, int len) in SymbolOccurrences(doc: doc, hit: hit))
        {
            int l = Math.Max(val1: 0, val2: line - 1);
            int c = Math.Max(val1: 0, val2: col - 1);
            edits.Add(item: new Dictionary<string, object?>
            {
                [key: PropRange] = new Dictionary<string, object?>
                {
                    [key: PropStart] =
                        new Dictionary<string, object?>
                        {
                            [key: "line"] = l, [key: PropCharacter] = c
                        },
                    [key: "end"] =
                        new Dictionary<string, object?>
                        {
                            [key: "line"] = l, [key: PropCharacter] = c + len
                        }
                },
                [key: "newText"] = newName
            });
        }

        WriteResult(stdout: stdout,
            id: id,
            result: new Dictionary<string, object?>
            {
                [key: "changes"] = new Dictionary<string, object?> { [key: uri] = edits }
            });
    }

    /// <summary>
    /// <c>textDocument/semanticTokens/full</c>: classify identifier tokens as <c>function</c> (a call
    /// callee at that position) or <c>variable</c>, delta-encoded per the LSP spec.
    /// </summary>
    private static void HandleSemanticTokens(Stream stdout, JsonElement id, JsonElement root)
    {
        if (!root.TryGetProperty(propertyName: PropParams, value: out JsonElement p) ||
            !p.TryGetProperty(propertyName: PropTextDocument, value: out JsonElement td) ||
            !td.TryGetProperty(propertyName: "uri", value: out JsonElement uriEl) ||
            !Docs.TryGetValue(key: uriEl.GetString() ?? "", value: out DocState? doc))
        {
            WriteResult(stdout: stdout,
                id: id,
                result: new Dictionary<string, object?> { [key: "data"] = new List<object?>() });
            return;
        }

        List<ISyntaxTreeNode> nodes = AllNodes(program: doc.Program);
        BuildSemanticRoleSets(nodes: nodes,
            functionPos: out HashSet<(int, int)> functionPos,
            typePos: out HashSet<(int, int)> typePos,
            variablePos: out HashSet<(int, int)> variablePos,
            deadPos: out HashSet<(int, int)> deadPos);

        List<object?> data = BuildDeltaEncodedTokens(doc: doc,
            functionPos: functionPos,
            typePos: typePos,
            variablePos: variablePos,
            deadPos: deadPos);

        WriteResult(stdout: stdout,
            id: id,
            result: new Dictionary<string, object?> { [key: "data"] = data });
    }

    /// <summary>Builds the four AST-derived position sets used to classify identifier tokens for semantic
    /// highlighting: call-callee positions (function), typed variable positions, type-name positions, and
    /// dead-use (moved-out) positions.</summary>
    private static void BuildSemanticRoleSets(List<ISyntaxTreeNode> nodes,
        out HashSet<(int, int)> functionPos, out HashSet<(int, int)> typePos,
        out HashSet<(int, int)> variablePos, out HashSet<(int, int)> deadPos)
    {
        functionPos = new HashSet<(int, int)>();
        typePos = new HashSet<(int, int)>();
        variablePos = new HashSet<(int, int)>();
        deadPos = new HashSet<(int, int)>();

        foreach (CallExpression call in nodes.OfType<CallExpression>())
        {
            if (call.Callee is IdentifierExpression cid)
            {
                functionPos.Add(item: (cid.Location.Line, cid.Location.Column));
            }
        }

        foreach (IdentifierExpression ide in nodes.OfType<IdentifierExpression>())
        {
            (int Line, int Column) key = (ide.Location.Line, ide.Location.Column);
            if (ide.IsDeadUse)
            {
                deadPos.Add(item: key); // read after its ownership was moved out — grey it out
            }

            if (ide.ResolvedVariable != null)
            {
                variablePos.Add(item: key);
            }
            else if (ide.ResolvedType is { } rt && IsTypeLikeName(name: ide.Name) &&
                     rt.Name == ide.Name)
            {
                typePos.Add(item: key);
            }
        }
    }

    /// <summary>Encodes the document's tokens as a delta-encoded LSP semantic token data array
    /// (deltaLine, deltaChar, length, tokenType, modifiers per token).</summary>
    private static List<object?> BuildDeltaEncodedTokens(DocState doc,
        HashSet<(int, int)> functionPos, HashSet<(int, int)> typePos,
        HashSet<(int, int)> variablePos, HashSet<(int, int)> deadPos)
    {
        var toks = doc.Tokens
                      .Where(predicate: t =>
                           t.Type != TokenType.Newline && t.Type != TokenType.Eof &&
                           t.Text.Length > 0)
                      .OrderBy(keySelector: t => t.Line)
                      .ThenBy(keySelector: t => t.Column)
                      .ToList();

        var data = new List<object?>();
        int prevLine = 0;
        int prevChar = 0;
        foreach (Token t in toks)
        {
            int type = ClassifyToken(t: t,
                functionPos: functionPos,
                typePos: typePos,
                variablePos: variablePos);
            if (type < 0)
            {
                continue; // operators / punctuation — left to the TextMate grammar
            }

            int line0 = t.Line - 1;
            int char0 = t.Column - 1;
            int deltaLine = line0 - prevLine;
            int deltaChar = deltaLine == 0
                ? char0 - prevChar
                : char0;
            int mods = deadPos.Contains(item: (t.Line, t.Column))
                ? ModDeprecated
                : 0;
            data.Add(item: deltaLine);
            data.Add(item: deltaChar);
            data.Add(item: t.Text.Length);
            data.Add(item: type);
            data.Add(item: mods);
            prevLine = line0;
            prevChar = char0;
        }

        return data;
    }

    /// <summary>
    /// Maps one token to a semantic-token legend index, or -1 to leave it to the TextMate grammar
    /// (operators / punctuation). Identifiers use the AST-derived role sets; everything else is
    /// classified structurally from its <see cref="TokenType"/>.
    /// </summary>
    private static int ClassifyToken(Token t, HashSet<(int, int)> functionPos,
        HashSet<(int, int)> typePos, HashSet<(int, int)> variablePos)
    {
        if (t.Type == TokenType.Identifier)
        {
            return ClassifyIdentifierToken(t: t,
                functionPos: functionPos,
                typePos: typePos,
                variablePos: variablePos);
        }

        return ClassifyNonIdentifierToken(t: t);
    }

    /// <summary>Classifies an <c>Identifier</c>-typed token using the AST-derived role sets, falling back
    /// to the PascalCase naming convention for unresolved names.</summary>
    private static int ClassifyIdentifierToken(Token t, HashSet<(int, int)> functionPos,
        HashSet<(int, int)> typePos, HashSet<(int, int)> variablePos)
    {
        (int, int) key = (t.Line, t.Column);
        if (functionPos.Contains(item: key))
        {
            return SemTok(name: "function");
        }

        if (variablePos.Contains(item: key))
        {
            return SemTok(name: "variable");
        }

        if (typePos.Contains(item: key))
        {
            return SemTok(name: "type");
        }

        // Unresolved bare identifier — fall back to the naming convention (PascalCase = type).
        return IsTypeLikeName(name: t.Text)
            ? SemTok(name: "type")
            : SemTok(name: "variable");
    }

    /// <summary>Classifies a non-identifier token as comment, string, number, keyword, or -1
    /// (operators / punctuation, left to the TextMate grammar).</summary>
    private static int ClassifyNonIdentifierToken(Token t)
    {
        string tn = t.Type.ToString();
        if (tn.Contains(value: "Comment"))
        {
            return SemTok(name: "comment");
        }

        if (t.Type is TokenType.TextLiteral or TokenType.RawText or TokenType.TextSegment
            or TokenType.CharacterLiteral)
        {
            return SemTok(name: "string");
        }

        // Numeric literals: the suffixed *Literal kinds AND the pre-resolution "UndecidedInteger" /
        // "UndecidedFloat" a bare literal starts as (its width is only chosen later, in SA).
        if (tn.EndsWith(value: "Literal", comparisonType: StringComparison.Ordinal) ||
            tn.StartsWith(value: "Undecided", comparisonType: StringComparison.Ordinal) ||
            char.IsDigit(c: t.Text[index: 0]))
        {
            return SemTok(name: "number");
        }

        // A word-shaped non-identifier token is a keyword (routine, entity, if, each, true, ...).
        if (t.Text.Length > 0 && (char.IsLetter(c: t.Text[index: 0]) || t.Text[index: 0] == '_'))
        {
            return SemTok(name: "keyword");
        }

        return -1; // operators / punctuation
    }

    /// <summary>Naming-convention heuristic: PascalCase identifiers denote types in RazorForge/Suflae.</summary>
    private static bool IsTypeLikeName(string name)
    {
        return name.Length > 0 && char.IsUpper(c: name[index: 0]);
    }

    // LSP SymbolKind numbers used below: File=1 Module=2 Namespace=3 Class=5 Method=6 Property=7 Field=8
    // Enum=10 Interface=11 FreeRoutine=12 Variable=13 Constant=14 Struct=23.
    private static (int Kind, bool IsType) SymbolKindOf(string declTypeName)
    {
        return declTypeName switch
        {
            NodeRoutineDeclaration => (12, false),
            "RecordDeclaration" => (23, true),
            "EntityDeclaration" => (5, true),
            "ChoiceDeclaration" => (10, true),
            "VariantDeclaration" => (10, true),
            "FlagsDeclaration" => (10, true),
            "CrashableDeclaration" => (10, true),
            "ProtocolDeclaration" => (11, true),
            "VariableDeclaration" => (13, false),
            _ => (12, false)
        };
    }

    /// <summary><c>textDocument/documentSymbol</c>: this file's declarations as an outline (flat
    /// DocumentSymbol list — routines, types, top-level variables), each ranged at its name.</summary>
    private static void HandleDocumentSymbol(Stream stdout, JsonElement id, JsonElement root)
    {
        if (!root.TryGetProperty(propertyName: PropParams, value: out JsonElement p) ||
            !p.TryGetProperty(propertyName: PropTextDocument, value: out JsonElement td) ||
            !td.TryGetProperty(propertyName: "uri", value: out JsonElement uriEl) ||
            !Docs.TryGetValue(key: uriEl.GetString() ?? "", value: out DocState? doc))
        {
            WriteResult(stdout: stdout, id: id, result: new List<object?>());
            return;
        }

        // Walk ONLY the top-level declarations (not AllNodes, which would descend into routine bodies and
        // list every local `var` in the outline). Type members (fields, cases) are nested as children.
        var syms = new List<object?>();
        foreach (ISyntaxTreeNode node in doc.Program.Declarations)
        {
            Dictionary<string, object?>? sym = MakeDocSymbol(node: node);
            if (sym == null)
            {
                continue;
            }

            var children = new List<object?>();
            foreach (object? member in MembersOf(node: node))
            {
                if (member is ISyntaxTreeNode mn && MakeDocSymbol(node: mn) is { } childSym)
                {
                    children.Add(item: childSym);
                }
            }

            if (children.Count > 0)
            {
                sym[key: "children"] = children;
            }

            syms.Add(item: sym);
        }

        WriteResult(stdout: stdout, id: id, result: syms);
    }

    /// <summary>A DocumentSymbol for a declaration node, or null if it is not a named declaration.</summary>
    private static Dictionary<string, object?>? MakeDocSymbol(ISyntaxTreeNode node)
    {
        string tn = node.GetType()
                        .Name;
        if (!tn.EndsWith(value: SuffixDeclaration, comparisonType: StringComparison.Ordinal) ||
            node is not SyntaxTreeNode sn || GetNameProp(node: node) is not { Length: > 0 } name)
        {
            return null;
        }

        (int kind, _) = SymbolKindOf(declTypeName: tn);
        int l = Math.Max(val1: 0, val2: sn.Location.Line - 1);
        int c = Math.Max(val1: 0, val2: sn.Location.Column - 1);
        var range = new Dictionary<string, object?>
        {
            [key: PropStart] =
                new Dictionary<string, object?>
                {
                    [key: "line"] = l, [key: PropCharacter] = c
                },
            [key: "end"] = new Dictionary<string, object?>
            {
                [key: "line"] = l, [key: PropCharacter] = c + name.Length
            }
        };
        return new Dictionary<string, object?>
        {
            [key: "name"] = name,
            [key: "kind"] = kind,
            [key: PropRange] = range,
            [key: "selectionRange"] = range
        };
    }

    /// <summary>The member declarations of a type node (its <c>Members</c> / <c>Cases</c> list), or empty.</summary>
    private static IEnumerable<object?> MembersOf(ISyntaxTreeNode node)
    {
        foreach (string prop in new[]
                 {
                     "Members",
                     "Cases"
                 })
        {
            object? value = node.GetType()
                                .GetProperty(name: prop,
                                     bindingAttr: System.Reflection.BindingFlags.Public |
                                                  System.Reflection.BindingFlags.Instance)
                               ?.GetValue(obj: node);
            if (value is System.Collections.IEnumerable seq and not string)
            {
                foreach (object? item in seq)
                {
                    yield return item;
                }
            }
        }
    }

    /// <summary><c>workspace/symbol</c>: registry routines + open-file declarations across all open
    /// documents whose name contains the query (case-insensitive), as located SymbolInformation.</summary>
    private static void HandleWorkspaceSymbol(Stream stdout, JsonElement id, JsonElement root)
    {
        string query = root.TryGetProperty(propertyName: PropParams, value: out JsonElement p) &&
                       p.TryGetProperty(propertyName: "query", value: out JsonElement q)
            ? q.GetString() ?? ""
            : "";

        var syms = new List<object?>();
        var seen = new HashSet<string>();

        foreach (DocState doc in Docs.Values)
        {
            foreach (RoutineInfo r in doc.Registry
                                         .GetAllRoutines()
                                         .Where(predicate: r => !r.Name.StartsWith(value: '$')))
            {
                TryAddWorkspaceSymbol(syms: syms,
                    seen: seen,
                    query: query,
                    name: r.Name,
                    kind: r.OwnerType != null
                        ? 6
                        : 12,
                    loc: r.Location);
            }

            foreach (ISyntaxTreeNode node in AllNodes(program: doc.Program))
            {
                string tn = node.GetType()
                                .Name;
                if (tn.EndsWith(value: SuffixDeclaration,
                        comparisonType: StringComparison.Ordinal) && node is SyntaxTreeNode sn &&
                    GetNameProp(node: node) is { } n)
                {
                    (int kind, _) = SymbolKindOf(declTypeName: tn);
                    TryAddWorkspaceSymbol(syms: syms,
                        seen: seen,
                        query: query,
                        name: n,
                        kind: kind,
                        loc: sn.Location);
                }
            }
        }

        WriteResult(stdout: stdout, id: id, result: syms);
    }

    /// <summary>Adds a workspace symbol to the accumulator if its name matches the query, it hasn't been
    /// seen before, and the cap of 300 symbols hasn't been reached.</summary>
    private static void TryAddWorkspaceSymbol(List<object?> syms, HashSet<string> seen,
        string query, string name, int kind,
        SourceLocation? loc)
    {
        if (loc == null || name.Length == 0 || query.Length > 0 &&
            !name.Contains(value: query, comparisonType: StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string key = $"{name}@{loc.FileName}:{loc.Line}";
        if (!seen.Add(item: key) || syms.Count >= 300)
        {
            return;
        }

        syms.Add(item: new Dictionary<string, object?>
        {
            [key: "name"] = name,
            [key: "kind"] = kind,
            [key: "location"] = LocationToLsp(loc: loc)
        });
    }

    /// <summary><c>textDocument/inlayHint</c>: for a <c>var x = expr</c> with no written type, an inferred
    /// <c>: Type</c> hint after the name (from the initializer's resolved type).</summary>
    private static void HandleInlayHint(Stream stdout, JsonElement id, JsonElement root)
    {
        if (!root.TryGetProperty(propertyName: PropParams, value: out JsonElement p) ||
            !p.TryGetProperty(propertyName: PropTextDocument, value: out JsonElement td) ||
            !td.TryGetProperty(propertyName: "uri", value: out JsonElement uriEl) ||
            !Docs.TryGetValue(key: uriEl.GetString() ?? "", value: out DocState? doc))
        {
            WriteResult(stdout: stdout, id: id, result: new List<object?>());
            return;
        }

        var hints = new List<object?>();

        void AddHint(int line1, int col1, string labelText,
            int kind, bool padLeft)
        {
            hints.Add(item: new Dictionary<string, object?>
            {
                [key: "position"] = new Dictionary<string, object?>
                {
                    [key: "line"] = line1 - 1, [key: PropCharacter] = col1 - 1
                },
                [key: PropLabel] = labelText,
                [key: "kind"] = kind,
                [key: "paddingLeft"] = padLeft
            });
        }

        foreach (ISyntaxTreeNode node in AllNodes(program: doc.Program))
        {
            switch (node)
            {
                // `var x = expr` with no written type → an inferred `: Type` hint after the name. Skip a
                // failed inference (`<error>`, e.g. reading a dead value) — a bogus hint is worse than none.
                case VariableDeclaration { Type: null, Initializer: { ResolvedType: { } vt } } vd
                    when !vt.Name.StartsWith(value: '<'):
                {
                    Token? nameTok = doc.Tokens.FirstOrDefault(predicate: t =>
                        t.Type == TokenType.Identifier && t.Text == vd.Name &&
                        t.Line == vd.Location.Line);
                    if (nameTok != null)
                    {
                        AddHint(line1: nameTok.Line,
                            col1: nameTok.Column + nameTok.Text.Length,
                            labelText: $": {vt.Name}",
                            kind: 1,
                            padLeft: false);
                    }

                    break;
                }

                // `steal x` → a "moved" marker after the stolen variable, so the ownership hand-off is
                // visible at the point the source binding dies.
                case StealExpression { Operand: IdentifierExpression sid }:
                    AddHint(line1: sid.Location.Line,
                        col1: sid.Location.Column + sid.Name.Length,
                        labelText: " ⟶ moved",
                        kind: 2,
                        padLeft: true);
                    break;
            }
        }

        WriteResult(stdout: stdout, id: id, result: hints);
    }

    /// <summary>
    /// <c>textDocument/codeAction</c>: quick-fixes for the diagnostics in range. Currently one — an
    /// unused Bool-returning call (RF-W007) gets a "Prepend discard" edit. The structure takes more.
    /// </summary>
    private static void HandleCodeAction(Stream stdout, JsonElement id, JsonElement root)
    {
        if (!root.TryGetProperty(propertyName: PropParams, value: out JsonElement p) ||
            !p.TryGetProperty(propertyName: PropTextDocument, value: out JsonElement td) ||
            !td.TryGetProperty(propertyName: "uri", value: out JsonElement uriEl) ||
            !Docs.TryGetValue(key: uriEl.GetString() ?? "", value: out DocState? doc))
        {
            WriteResult(stdout: stdout, id: id, result: new List<object?>());
            return;
        }

        string uri = uriEl.GetString() ?? "";
        var actions = new List<object?>();

        if (p.TryGetProperty(propertyName: "context", value: out JsonElement ctx) &&
            ctx.TryGetProperty(propertyName: "diagnostics", value: out JsonElement diags) &&
            diags.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement diag in diags.EnumerateArray())
            {
                TryAddDiscardCodeAction(actions: actions,
                    doc: doc,
                    uri: uri,
                    diag: diag);
            }
        }

        WriteResult(stdout: stdout, id: id, result: actions);
    }

    /// <summary>If <paramref name="diag"/> is an RF-W007 (unused Bool-returning call) diagnostic and the
    /// first token on its line can be found, adds a "Prepend 'discard'" quick-fix code action.</summary>
    private static void TryAddDiscardCodeAction(List<object?> actions, DocState doc, string uri,
        JsonElement diag)
    {
        string code;
        if (!diag.TryGetProperty(propertyName: "code", value: out JsonElement cd))
        {
            code = "";
        }
        else if (cd.ValueKind == JsonValueKind.String)
        {
            code = cd.GetString() ?? "";
        }
        else
        {
            code = cd.ToString();
        }

        if (!code.Contains(value: "W007") ||
            !diag.TryGetProperty(propertyName: PropRange, value: out JsonElement dr) ||
            !dr.TryGetProperty(propertyName: PropStart, value: out JsonElement ds) ||
            !ds.TryGetProperty(propertyName: "line", value: out JsonElement dl))
        {
            return;
        }

        int line1 = dl.GetInt32() + 1;
        Token? first = doc.Tokens
                          .Where(predicate: t => t.Line == line1 && t.Text.Length > 0 &&
                                                 t.Type != TokenType.Newline)
                          .OrderBy(keySelector: t => t.Column)
                          .FirstOrDefault();
        if (first == null)
        {
            return;
        }

        var pos = new Dictionary<string, object?>
        {
            [key: "line"] = line1 - 1, [key: PropCharacter] = first.Column - 1
        };
        actions.Add(item: new Dictionary<string, object?>
        {
            [key: "title"] = "Prepend 'discard'",
            [key: "kind"] = "quickfix",
            [key: "diagnostics"] = new List<object?> { JsonElementToObject(el: diag) },
            [key: "edit"] = new Dictionary<string, object?>
            {
                [key: "changes"] = new Dictionary<string, object?>
                {
                    [key: uri] = new List<object?>
                    {
                        new Dictionary<string, object?>
                        {
                            [key: PropRange] = new Dictionary<string, object?>
                            {
                                [key: PropStart] = pos, [key: "end"] = pos
                            },
                            [key: "newText"] = "discard "
                        }
                    }
                }
            }
        });
    }

    /// <summary>Shallow-copies a diagnostic JsonElement back into a serializable dictionary (so a code
    /// action can echo the diagnostic it fixes).</summary>
    private static Dictionary<string, object?> JsonElementToObject(JsonElement el)
    {
        var d = new Dictionary<string, object?>();
        if (el.TryGetProperty(propertyName: PropRange, value: out JsonElement r) &&
            r.TryGetProperty(propertyName: PropStart, value: out JsonElement s) &&
            r.TryGetProperty(propertyName: "end", value: out JsonElement e))
        {
            d[key: PropRange] = new Dictionary<string, object?>
            {
                [key: PropStart] = new Dictionary<string, object?>
                {
                    [key: "line"] = s.GetProperty(propertyName: "line")
                                     .GetInt32(),
                    [key: PropCharacter] = s.GetProperty(propertyName: PropCharacter)
                                            .GetInt32()
                },
                [key: "end"] = new Dictionary<string, object?>
                {
                    [key: "line"] = e.GetProperty(propertyName: "line")
                                     .GetInt32(),
                    [key: PropCharacter] = e.GetProperty(propertyName: PropCharacter)
                                            .GetInt32()
                }
            };
        }

        if (el.TryGetProperty(propertyName: "message", value: out JsonElement m))
        {
            d[key: "message"] = m.GetString();
        }

        if (el.TryGetProperty(propertyName: "severity", value: out JsonElement sev) &&
            sev.ValueKind == JsonValueKind.Number)
        {
            d[key: "severity"] = sev.GetInt32();
        }

        return d;
    }

    private static void AddItem(List<Dictionary<string, object?>> items, HashSet<string> seen,
        string label, int kind, string? detail = null,
        string? documentation = null)
    {
        if (label.Length == 0 || !seen.Add(item: label))
        {
            return;
        }

        var item =
            new Dictionary<string, object?> { [key: PropLabel] = label, [key: "kind"] = kind };
        if (detail != null)
        {
            item[key: "detail"] = detail;
        }

        if (!string.IsNullOrWhiteSpace(value: documentation))
        {
            item[key: PropDocumentation] = new Dictionary<string, object?>
            {
                [key: "kind"] = PropMarkdown, [key: PropValue] = RenderDoc(doc: documentation)
            };
        }

        items.Add(item: item);
    }

    /// <summary>A doc-comment parsed into its summary prose and reStructuredText-style field lists.</summary>
    private sealed record DocInfo(
        string Summary,
        List<(string Name, string Desc)> Params,
        List<(string Name, string Desc)> TypeParams,
        string? Returns,
        string? Throws,
        string? Absent,
        List<string> Notes,
        List<string> Sees);

    /// <summary>
    /// Parses a stored <c>###</c> doc-comment into a summary plus the field lists the docs use:
    /// <c>:param name:</c>, <c>:typeparam Name:</c>, <c>:returns:</c>, <c>:throws:</c>, <c>:absent:</c>,
    /// <c>:note:</c>, <c>:see:</c>. Lines before the first field are the summary; a line that does not
    /// open a new <c>:field:</c> continues the previous field's (or the summary's) text.
    /// </summary>
    private static DocInfo ParseDoc(string doc)
    {
        var summary = new List<string>();
        var pars = new List<(string, string)>();
        var typePars = new List<(string, string)>();
        string? returns = null, throws = null, absent = null;
        var notes = new List<string>();
        var sees = new List<string>();

        // Where the last field's continuation text goes; null = still in the summary.
        Action<string>? append = null;

        foreach (string raw in doc.Replace(oldValue: "\r", newValue: "")
                                  .Split(separator: '\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith(value: ':') &&
                line.IndexOf(value: ':', startIndex: 1) is var sc and > 0)
            {
                string spec = line[1..sc]
                   .Trim();
                string desc = line[(sc + 1)..]
                   .Trim();
                string[] parts = spec.Split(separator: ' ',
                    count: 2,
                    options: StringSplitOptions.RemoveEmptyEntries);
                string kind = parts[0]
                   .ToLowerInvariant();
                string? name = parts.Length > 1
                    ? parts[1]
                    : null;

                switch (kind)
                {
                    case "param" when name != null:
                        pars.Add(item: (name, desc));
                        int pi = pars.Count - 1;
                        append = s => pars[index: pi] = (pars[index: pi].Item1,
                            $"{pars[index: pi].Item2} {s}".Trim());
                        break;
                    case "typeparam" when name != null:
                        typePars.Add(item: (name, desc));
                        int ti = typePars.Count - 1;
                        append = s => typePars[index: ti] = (typePars[index: ti].Item1,
                            $"{typePars[index: ti].Item2} {s}".Trim());
                        break;
                    case "returns":
                        returns = desc;
                        append = s => returns = $"{returns} {s}".Trim();
                        break;
                    case "throws":
                        throws = desc;
                        append = s => throws = $"{throws} {s}".Trim();
                        break;
                    case "absent":
                        absent = desc;
                        append = s => absent = $"{absent} {s}".Trim();
                        break;
                    case "note":
                        notes.Add(item: desc);
                        int ni = notes.Count - 1;
                        append = s => notes[index: ni] = $"{notes[index: ni]} {s}".Trim();
                        break;
                    case "see":
                        sees.Add(item: desc);
                        int si = sees.Count - 1;
                        append = s => sees[index: si] = $"{sees[index: si]} {s}".Trim();
                        break;
                    default:
                        // Unknown `:field:` — keep it verbatim in the summary so nothing is lost.
                        summary.Add(item: line);
                        append = null;
                        break;
                }
            }
            else if (append != null && line.Length > 0)
            {
                append(obj: line);
            }
            else
            {
                summary.Add(item: line);
            }
        }

        return new DocInfo(Summary: string.Join(separator: "\n", values: summary)
                                          .Trim(),
            Params: pars,
            TypeParams: typePars,
            Returns: returns,
            Throws: throws,
            Absent: absent,
            Notes: notes,
            Sees: sees);
    }

    /// <summary>Renders a parsed doc-comment as hover/completion markdown: the summary prose, then a
    /// bulleted parameters/type-parameters block and labelled Returns/Throws/Absent/Note/See lines.</summary>
    private static string RenderDoc(string doc)
    {
        DocInfo d = ParseDoc(doc: doc);
        var sb = new StringBuilder();
        if (d.Summary.Length > 0)
        {
            sb.Append(value: d.Summary);
        }

        void Section(string title, IEnumerable<(string Name, string Desc)> entries)
        {
            var list = entries.ToList();
            if (list.Count == 0)
            {
                return;
            }

            if (sb.Length > 0)
            {
                sb.Append(value: "\n\n");
            }

            sb.Append(value: $"**{title}**");
            foreach ((string name, string desc) in list)
            {
                sb.Append(value: desc.Length > 0
                    ? $"\n- `{name}` — {desc}"
                    : $"\n- `{name}`");
            }
        }

        void Line(string label, string? text)
        {
            if (string.IsNullOrWhiteSpace(value: text))
            {
                return;
            }

            sb.Append(value: sb.Length > 0
                ? "\n\n"
                : "");
            sb.Append(value: $"**{label}** — {text}");
        }

        Section(title: "Type parameters", entries: d.TypeParams);
        Section(title: "Parameters", entries: d.Params);
        Line(label: "Returns", text: d.Returns);
        Line(label: "Throws", text: d.Throws);
        Line(label: "Absent", text: d.Absent);
        foreach (string note in d.Notes)
        {
            Line(label: "Note", text: note);
        }

        foreach (string see in d.Sees)
        {
            Line(label: "See", text: see);
        }

        return sb.ToString();
    }

    /// <summary>A routine's signature for the completion detail: <c>(a: T, b: U) -> R</c> (with `!` if failable).</summary>
    private static string RoutineDetail(RoutineInfo r)
    {
        string ps = string.Join(separator: ", ",
            values: r.Parameters.Select(selector: p => $"{p.Name}: {p.Type.Name}"));
        string ret = r.ReturnType != null
            ? $" -> {r.ReturnType.Name}"
            : "";
        string bang = r.IsFailable
            ? "!"
            : "";
        return $"{bang}({ps}){ret}";
    }

    /// <summary>
    /// If the cursor is in a <c>receiver.</c> member position (right after the dot, or partway through a
    /// member name), the receiver identifier token — else null. This is a pure token check, so it holds
    /// even when the line is mid-edit (<c>p.</c> with nothing after the dot yet), which is exactly when
    /// completion fires. Callers use "in member context" to suppress the global fallback after a dot.
    /// </summary>
    private static Token? MemberReceiverToken(DocState doc, int line0, int char0)
    {
        int line1 = line0 + 1;
        int col1 = char0 + 1;
        var before = doc.Tokens
                        .Where(predicate: t => t.Line == line1 && t.Column < col1)
                        .OrderBy(keySelector: t => t.Column)
                        .ToList();
        if (before.Count == 0)
        {
            return null;
        }

        // `receiver.`  (cursor right after the dot) or `receiver.parti` (typing a member name).
        Token? receiver = null;
        if (before[^1].Text == "." && before.Count >= 2)
        {
            receiver = before[^2];
        }
        else if (before.Count >= 3 && before[^2].Text == ".")
        {
            receiver = before[^3];
        }

        // A plain identifier receiver, or the `me` keyword (`me.` completes the enclosing type's members).
        return receiver is { Type: TokenType.Identifier } or { Type: TokenType.Me }
            ? receiver
            : null;
    }

    /// <summary>
    /// If the cursor is right after a <c>Realm::</c> qualifier (<c>C::</c>, <c>LLVM::</c>, …) — after the
    /// <c>::</c> or partway through the qualified name — the realm tag text; else null. Used to scope
    /// completion to that realm's routines instead of the global dump.
    /// </summary>
    private static string? RealmQualifierBefore(DocState doc, int line0, int char0)
    {
        int line1 = line0 + 1;
        int col1 = char0 + 1;
        var before = doc.Tokens
                        .Where(predicate: t => t.Line == line1 && t.Column < col1)
                        .OrderBy(keySelector: t => t.Column)
                        .ToList();

        // `Realm::` (cursor after the ::) or `Realm::par` (typing the qualified name).
        if (before.Count >= 2 && before[^1].Type == TokenType.DoubleColon &&
            before[^2].Type == TokenType.Identifier)
        {
            return before[^2].Text;
        }

        if (before.Count >= 3 && before[^2].Type == TokenType.DoubleColon &&
            before[^3].Type == TokenType.Identifier)
        {
            return before[^3].Text;
        }

        return null;
    }

    /// <summary>
    /// The resolved type of a receiver identifier token, for member completion. Tries, in order: the
    /// typed <see cref="IdentifierExpression"/> at that exact position; then — since a half-typed
    /// <c>p.</c> may leave that node untyped — any same-named identifier's stamped variable binding or
    /// resolved type elsewhere in the file. Returns null only if the name has no known type at all.
    /// </summary>
    private static TypeInfo? ReceiverType(DocState doc, Token receiver)
    {
        // `me.` → the enclosing type. Resolve it from the nearest routine DECLARED above the cursor in
        // THIS file that has an owner — robust even on a half-typed `me.` line where the `me` node itself
        // may not have been analyzed. (`me`'s type is never a local binding, so the identifier searches
        // below would miss it.)
        if (receiver.Text == "me")
        {
            string? file = doc.Tokens.FirstOrDefault()
                             ?.FileName;
            RoutineInfo? enclosing = doc.Registry
                                        .GetAllRoutines()
                                        .Where(predicate: r =>
                                             r.OwnerType != null && r.Location is { } l &&
                                             l.FileName == file && l.Line <= receiver.Line)
                                        .OrderByDescending(keySelector: r => r.Location!.Line)
                                        .FirstOrDefault();
            if (enclosing?.OwnerType is { } owner)
            {
                return owner;
            }
        }

        var idents = AllNodes(program: doc.Program)
                    .OfType<IdentifierExpression>()
                    .ToList();

        // 1. The receiver identifier at exactly this position (NOT the enclosing MemberExpression, which
        //    shares the column but carries the MEMBER's type).
        TypeInfo? exactType = idents.FirstOrDefault(predicate: e =>
                                         e.ResolvedType != null && e.Name == receiver.Text &&
                                         e.Location.Line == receiver.Line &&
                                         e.Location.Column == receiver.Column)
                                   ?.ResolvedType;
        if (exactType != null)
        {
            return exactType;
        }

        // 2. Fallback for a mid-edit line: the same name's binding (or any typed use) elsewhere.
        TypeInfo? bindingType = idents
                               .FirstOrDefault(predicate: e =>
                                    e.Name == receiver.Text && e.ResolvedVariable != null)
                              ?.ResolvedVariable?.Type;
        if (bindingType != null)
        {
            return bindingType;
        }

        return idents
              .FirstOrDefault(predicate: e => e.Name == receiver.Text && e.ResolvedType != null)
             ?.ResolvedType;
    }

    /// <summary>
    /// Whether a resolved member routine actually applies to this receiver. A method declared on a
    /// SPECIALIZED receiver — e.g. <c>routine List[Agent[V]].gather()</c> — registers under the generic
    /// <c>List</c> owner but its <see cref="RoutineInfo.MeType"/> pins the element to a concrete type. It
    /// must NOT be offered for an unrelated instantiation like <c>List[FaceDraw]</c>. A method whose
    /// receiver pattern is the bare generic (element is a generic parameter) applies to any instantiation.
    /// </summary>
    private static bool ReceiverAcceptsMethod(RoutineInfo mr, TypeInfo receiverType)
    {
        if (mr.MeType is not { TypeArguments: { Count: > 0 } meArgs } ||
            receiverType.TypeArguments is not { Count: > 0 } recvArgs ||
            recvArgs.Count != meArgs.Count)
        {
            return true; // no comparable specialization — don't over-filter
        }

        for (int i = 0; i < meArgs.Count; i++)
        {
            // A generic-parameter slot in the receiver pattern (e.g. the `T` of `List[T]`) matches
            // anything. A CONCRETE pattern element (e.g. `Agent[V]`) requires the receiver's element to
            // be the same base type.
            if (meArgs[index: i] is not GenericParameterTypeInfo &&
                meArgs[index: i].BareName != recvArgs[index: i].BareName)
            {
                return false;
            }
        }

        return true;
    }

    private static IEnumerable<(string Name, string Type)> MemberVariableSignatures(TypeInfo type,
        bool includeSecret)
    {
        IEnumerable<MemberVariableInfo> members = type switch
        {
            EntityTypeInfo en => en.MemberVariables,
            RecordTypeInfo re => re.MemberVariables,
            _ => Enumerable.Empty<MemberVariableInfo>()
        };

        // `secret` fields are file-private (e.g. List's internal data/count/capacity buffer) — never offer
        // them to an outside `x.` completion. `posted` (open read / secret write) stays visible.
        return members
              .Where(predicate: v => includeSecret || v.Visibility != VisibilityModifier.Secret)
              .Select(selector: v => (v.Name, v.Type.Name));
    }

    /// <summary>An LSP Location for a 1-based (line, column) span of <paramref name="length"/> chars.</summary>
    private static Dictionary<string, object?> RangeLsp(string uri, int line1, int col1,
        int length)
    {
        int l = Math.Max(val1: 0, val2: line1 - 1);
        int c = Math.Max(val1: 0, val2: col1 - 1);
        return new Dictionary<string, object?>
        {
            [key: "uri"] = uri,
            [key: PropRange] = new Dictionary<string, object?>
            {
                [key: PropStart] =
                    new Dictionary<string, object?>
                    {
                        [key: "line"] = l, [key: PropCharacter] = c
                    },
                [key: "end"] = new Dictionary<string, object?>
                {
                    [key: "line"] = l, [key: PropCharacter] = c + length
                }
            }
        };
    }

    private static (string? Name, SourceLocation? Location) CalleeName(Expression callee)
    {
        return callee switch
        {
            IdentifierExpression id => (id.Name, id.Location),
            MemberExpression m => (m.MemberName, m.Location),
            _ => (null, null)
        };
    }

    private static string? GetNameProp(ISyntaxTreeNode node)
    {
        System.Reflection.PropertyInfo? p = node.GetType()
                                                .GetProperty(name: "Name",
                                                     bindingAttr: System.Reflection.BindingFlags
                                                        .Public | System.Reflection.BindingFlags
                                                        .Instance);
        return p != null && p.PropertyType == typeof(string)
            ? p.GetValue(obj: node) as string
            : null;
    }

    private static Dictionary<string, object?> LocationToLsp(SourceLocation loc)
    {
        int l = Math.Max(val1: 0, val2: loc.Line - 1);
        int c = Math.Max(val1: 0, val2: loc.Column - 1);
        return new Dictionary<string, object?>
        {
            [key: "uri"] = FileNameToUri(fileName: loc.FileName),
            [key: PropRange] = new Dictionary<string, object?>
            {
                [key: PropStart] =
                    new Dictionary<string, object?>
                    {
                        [key: "line"] = l, [key: PropCharacter] = c
                    },
                [key: "end"] = new Dictionary<string, object?>
                {
                    [key: "line"] = l, [key: PropCharacter] = c
                }
            }
        };
    }

    private static string FileNameToUri(string fileName)
    {
        try
        {
            if (fileName.StartsWith(value: "file:",
                    comparisonType: StringComparison.OrdinalIgnoreCase))
            {
                return fileName;
            }

            return new Uri(uriString: Path.GetFullPath(path: fileName)).AbsoluteUri;
        }
        catch
        {
            return fileName;
        }
    }

    /// <summary>Reflectively gathers every syntax-tree node reachable from <paramref name="node"/>.</summary>
    private static void CollectAllNodes(object? node, List<ISyntaxTreeNode> acc,
        HashSet<object> seen)
    {
        if (node == null || !seen.Add(item: node))
        {
            return;
        }

        if (node is ISyntaxTreeNode n)
        {
            acc.Add(item: n);
        }

        foreach (System.Reflection.PropertyInfo prop in node.GetType()
                                                            .GetProperties(
                                                                 bindingAttr: System.Reflection
                                                                    .BindingFlags.Public |
                                                                 System.Reflection.BindingFlags
                                                                    .Instance))
        {
            if (prop.GetIndexParameters()
                    .Length > 0)
            {
                continue; // skip indexers
            }

            object? value;
            try
            {
                value = prop.GetValue(obj: node);
            }
            catch
            {
                continue;
            }

            TraversePropertyValue(value: value, acc: acc, seen: seen);
        }
    }

    /// <summary>Descends into a single property value: recursing if it is a node, or iterating and
    /// recursing into each element if it is a non-string enumerable.</summary>
    private static void TraversePropertyValue(object? value, List<ISyntaxTreeNode> acc,
        HashSet<object> seen)
    {
        switch (value)
        {
            case ISyntaxTreeNode child:
                CollectAllNodes(node: child, acc: acc, seen: seen);
                break;
            case System.Collections.IEnumerable seq and not string:
                foreach (object? item in seq)
                {
                    if (item is ISyntaxTreeNode c)
                    {
                        CollectAllNodes(node: c, acc: acc, seen: seen);
                    }
                }

                break;
        }
    }

    /// <summary>All syntax-tree nodes of a document, computed once per hover/definition request.</summary>
    private static List<ISyntaxTreeNode> AllNodes(SyntaxTree.Program program)
    {
        var acc = new List<ISyntaxTreeNode>();
        CollectAllNodes(node: program,
            acc: acc,
            seen: new HashSet<object>(comparer: ReferenceEqualityComparer.Instance));
        return acc;
    }

    /// <summary>The token whose 1-based span contains the cursor (0-based LSP line/character), or null.</summary>
    private static Token? TokenAt(DocState doc, int line0, int char0)
    {
        int line1 = line0 + 1;
        int col1 = char0 + 1;
        foreach (Token tok in doc.Tokens)
        {
            if (tok.Line == line1 && tok.Column <= col1 &&
                col1 < tok.Column + Math.Max(val1: 1, val2: tok.Text.Length))
            {
                return tok;
            }
        }

        return null;
    }

    private static bool IsIdentifierText(string text)
    {
        if (text.Length == 0 || !(char.IsLetter(c: text[index: 0]) || text[index: 0] == '_'))
        {
            return false;
        }

        foreach (char c in text)
        {
            if (!(char.IsLetterOrDigit(c: c) || c == '_'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryReadPosition(JsonElement root, out string uri, out int line0,
        out int char0)
    {
        uri = "";
        line0 = 0;
        char0 = 0;
        if (!root.TryGetProperty(propertyName: PropParams, value: out JsonElement p) ||
            !p.TryGetProperty(propertyName: PropTextDocument, value: out JsonElement td) ||
            !td.TryGetProperty(propertyName: "uri", value: out JsonElement uriEl) ||
            !p.TryGetProperty(propertyName: "position", value: out JsonElement pos))
        {
            return false;
        }

        uri = uriEl.GetString() ?? "";
        line0 = pos.TryGetProperty(propertyName: "line", value: out JsonElement l)
            ? l.GetInt32()
            : 0;
        char0 = pos.TryGetProperty(propertyName: PropCharacter, value: out JsonElement c)
            ? c.GetInt32()
            : 0;
        return true;
    }

    /// <summary>Full-sync didChange: the last content change holds the entire document text.</summary>
    private static string? ExtractFullChangeText(JsonElement paramsEl)
    {
        if (!paramsEl.TryGetProperty(propertyName: "contentChanges",
                value: out JsonElement changes) || changes.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        string? last = null;
        foreach (JsonElement change in changes.EnumerateArray())
        {
            if (change.TryGetProperty(propertyName: "text", value: out JsonElement ct))
            {
                last = ct.GetString();
            }
        }

        return last;
    }

    /// <summary>
    /// Runs the front end (tokenize → parse → SA against the cached stdlib snapshot) on the
    /// document text and maps every parse error, semantic error, and warning to an LSP diagnostic.
    /// Any unexpected exception in the pipeline becomes a single diagnostic rather than crashing
    /// the server.
    /// </summary>
    private static List<Dictionary<string, object?>> Analyze(string uri, string text)
    {
        var diagnostics = new List<Dictionary<string, object?>>();
        bool isSuflae =
            uri.EndsWith(value: ".sf", comparisonType: StringComparison.OrdinalIgnoreCase);
        Language lang = isSuflae
            ? Language.Suflae
            : Language.RazorForge;
        string fileName = UriToFileName(uri: uri);

        try
        {
            var tokenizer = new Tokenizer(source: text, fileName: fileName, language: lang);
            List<Token> tokens = tokenizer.Tokenize();

            var parser = new Parser(tokens: tokens, language: lang, fileName: fileName);
            SyntaxTree.Program program = parser.Parse();

            // Underline the whole token at the reported position, not a single caret column.
            int SpanLen(int line, int col)
            {
                return tokens.FirstOrDefault(predicate: t => t.Line == line && t.Column == col)
                            ?.Text.Length ?? 1;
            }

            foreach (GrammarException pe in parser.GetStructuredErrors())
            {
                diagnostics.Add(item: MakeDiagnostic(line: pe.Line,
                    column: pe.Column,
                    severity: 1,
                    code: pe.Code.ToCodeString(language: lang),
                    message: pe.RawMessage,
                    length: SpanLen(line: pe.Line, col: pe.Column)));
            }

            TypeRegistry.StdlibSnapshot snapshot = isSuflae
                ? SfSnapshot.Value
                : RfSnapshot.Value;
            var verifier =
                new SemanticVerifier(language: lang, snapshot: snapshot) { SaOnly = true };
            AnalysisResult result = verifier.Analyze(program: program);

            foreach (SemanticError e in result.Errors)
            {
                diagnostics.Add(item: MakeDiagnostic(line: e.Location.Line,
                    column: e.Location.Column,
                    severity: 1,
                    code: e.Code.ToCodeString(),
                    message: e.Message,
                    length: SpanLen(line: e.Location.Line, col: e.Location.Column)));
            }

            foreach (SemanticWarning w in result.Warnings)
            {
                diagnostics.Add(item: MakeDiagnostic(line: w.Location.Line,
                    column: w.Location.Column,
                    severity: 2,
                    code: w.Code.ToCodeString(),
                    message: w.Message,
                    length: SpanLen(line: w.Location.Line, col: w.Location.Column)));
            }

            // Keep the typed AST + tokens + registry so hover/definition/completion reuse this analysis.
            Docs[key: uri] = new DocState(Program: program,
                Tokens: tokens,
                Lang: lang,
                Registry: verifier.Registry);
        }
        catch (GrammarException ex)
        {
            diagnostics.Add(item: MakeDiagnostic(line: ex.Line,
                column: ex.Column,
                severity: 1,
                code: ex.Code.ToCodeString(language: lang),
                message: ex.RawMessage));
        }
        catch (Exception ex)
        {
            // Never let an analyzer bug take down the server — surface it at the file head.
            diagnostics.Add(item: MakeDiagnostic(line: 1,
                column: 1,
                severity: 1,
                code: "RF-LSP",
                message: $"internal analyzer error: {ex.Message}"));
        }

        return diagnostics;
    }

    /// <summary>
    /// Builds an LSP Diagnostic. LSP positions are 0-based; our locations are 1-based. The range
    /// underlines a single character at the reported column (a token-length range is a later refinement).
    /// </summary>
    private static Dictionary<string, object?> MakeDiagnostic(int line, int column, int severity,
        string code, string message, int length = 1)
    {
        int l = Math.Max(val1: 0, val2: line - 1);
        int c = Math.Max(val1: 0, val2: column - 1);
        return new Dictionary<string, object?>
        {
            [key: PropRange] = new Dictionary<string, object?>
            {
                [key: PropStart] =
                    new Dictionary<string, object?>
                    {
                        [key: "line"] = l, [key: PropCharacter] = c
                    },
                [key: "end"] = new Dictionary<string, object?>
                {
                    [key: "line"] = l,
                    [key: PropCharacter] = c + Math.Max(val1: 1, val2: length)
                }
            },
            [key: "severity"] = severity, // 1 = Error, 2 = Warning
            [key: "code"] = code,
            [key: "source"] = "razorforge",
            [key: "message"] = message
        };
    }

    private static void PublishDiagnostics(Stream stdout, string uri,
        List<Dictionary<string, object?>> diagnostics)
    {
        WriteNotification(stdout: stdout,
            method: "textDocument/publishDiagnostics",
            @params: new Dictionary<string, object?>
            {
                [key: "uri"] = uri, [key: "diagnostics"] = diagnostics
            });
    }

    // ── JSON-RPC framing ────────────────────────────────────────────────────────────────────

    private static void WriteResult(Stream stdout, JsonElement id, object? result)
    {
        WriteMessage(stdout: stdout,
            payload: new Dictionary<string, object?>
            {
                [key: "jsonrpc"] = "2.0", [key: "id"] = id, [key: "result"] = result
            });
    }

    private static void WriteNotification(Stream stdout, string method, object @params)
    {
        WriteMessage(stdout: stdout,
            payload: new Dictionary<string, object?>
            {
                [key: "jsonrpc"] = "2.0", [key: "method"] = method, [key: PropParams] = @params
            });
    }

    private static void WriteMessage(Stream stdout, object payload)
    {
        string json = JsonSerializer.Serialize(value: payload, options: JsonOptions);
        byte[] bodyBytes = Encoding.UTF8.GetBytes(s: json);
        byte[] header = Encoding.ASCII.GetBytes(s: $"Content-Length: {bodyBytes.Length}\r\n\r\n");
        stdout.Write(buffer: header, offset: 0, count: header.Length);
        stdout.Write(buffer: bodyBytes, offset: 0, count: bodyBytes.Length);
        stdout.Flush();
    }

    /// <summary>Reads one Content-Length-framed message body, or null at EOF.</summary>
    private static byte[]? ReadMessage(Stream stdin)
    {
        int contentLength = -1;
        while (true)
        {
            string? line = ReadHeaderLine(stdin: stdin);
            if (line == null)
            {
                return null; // EOF mid-headers
            }

            if (line.Length == 0)
            {
                break; // blank line ends the header block
            }

            int colon = line.IndexOf(value: ':');
            if (colon > 0 && line[..colon]
                            .Trim()
                            .Equals(value: "Content-Length",
                                 comparisonType: StringComparison.OrdinalIgnoreCase) &&
                !int.TryParse(s: line[(colon + 1)..]
                       .Trim(),
                    result: out contentLength))
            {
                contentLength = -1; // malformed Content-Length header — treat as absent
            }
        }

        if (contentLength < 0)
        {
            return null;
        }

        byte[] buffer = new byte[contentLength];
        int read = 0;
        while (read < contentLength)
        {
            int n = stdin.Read(buffer: buffer, offset: read, count: contentLength - read);
            if (n <= 0)
            {
                return null; // EOF mid-body
            }

            read += n;
        }

        return buffer;
    }

    /// <summary>Reads one CRLF-terminated header line (byte by byte), stripped of the trailing CRLF.</summary>
    private static string? ReadHeaderLine(Stream stdin)
    {
        var sb = new StringBuilder();
        int prev = -1;
        while (true)
        {
            int b = stdin.ReadByte();
            if (b < 0)
            {
                return sb.Length == 0
                    ? null
                    : sb.ToString();
            }

            if (prev == '\r' && b == '\n')
            {
                sb.Length -= 1; // drop the '\r' already appended
                return sb.ToString();
            }

            sb.Append(value: (char)b);
            prev = b;
        }
    }

    /// <summary>file:// URI → a plain path for the tokenizer/parser (best-effort; used only for messages).</summary>
    private static string UriToFileName(string uri)
    {
        if (Uri.TryCreate(uriString: uri, uriKind: UriKind.Absolute, result: out Uri? parsed) &&
            parsed.IsFile)
        {
            return parsed.LocalPath;
        }

        return uri;
    }
}
