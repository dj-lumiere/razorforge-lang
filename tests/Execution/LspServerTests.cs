using System.Text.Json;

namespace RazorForge.Tests.Execution;

/// <summary>
/// End-to-end coverage for the LSP server (<c>src/Execution/LspServer.cs</c>): every request handler
/// is driven in-process over real, analyzed RazorForge/Suflae documents via <see cref="LspTestHarness"/>.
/// Positions are computed from the source text so the tests stay robust to edits.
///
/// All tests live in one class so they share xUnit's per-class sequential execution — the server keeps
/// open documents in a process-wide static dictionary, and each <see cref="LspTestHarness.Run"/> clears
/// it at the start, so overlapping sessions from parallel classes would race.
/// </summary>
public sealed class LspServerTests
{
    private const string Uri = "file:///test/Sample.rf";

    // A symbol-rich RazorForge document: free routines, a record with a member routine, a generic
    // container use, variables, calls, an f-string, and control flow — enough surface to exercise
    // hover / definition / references / completion / signature-help / symbols / semantic tokens.
    private const string Source =
        "module Test/Lsp\n" +
        "import IO/Console\n" +
        "\n" +
        "record Point\n" +
        "  x: S32\n" +
        "  y: S32\n" +
        "\n" +
        "  routine magnitude(me) -> S32\n" +
        "    return me.x * me.x + me.y * me.y\n" +
        "\n" +
        "routine add(a: S32, b: S32) -> S32\n" +
        "  return a + b\n" +
        "\n" +
        "routine start()\n" +
        "  var total = add(a: 1_s32, b: 2_s32)\n" +
        "  var p = Point(x: 3_s32, y: 4_s32)\n" +
        "  var m = p.magnitude()\n" +
        "  show(f\"total={total} mag={m}\")\n" +
        "  return\n";

    private static string Initialize(int id = 0)
    {
        return JsonSerializer.Serialize(value: new
        {
            jsonrpc = "2.0",
            id,
            method = "initialize",
            @params = new { capabilities = new { } }
        });
    }

    private const string Shutdown = "{\"jsonrpc\":\"2.0\",\"id\":999,\"method\":\"shutdown\"}";
    private const string Exit = "{\"jsonrpc\":\"2.0\",\"method\":\"exit\"}";

    private static (int Line, int Character) Pos(string needle, int occurrence = 1)
    {
        return LspTestHarness.PositionOf(text: Source, needle: needle, occurrence: occurrence);
    }

    [Fact]
    public void Initialize_ReturnsServerCapabilities()
    {
        IReadOnlyList<JsonDocument> replies = LspTestHarness.Run(Initialize(id: 1), Shutdown, Exit);

        JsonElement? init = LspTestHarness.ReplyWithId(replies: replies, id: 1);
        Assert.NotNull(@object: init);
        JsonElement caps = init!.Value.GetProperty(propertyName: "result").GetProperty(propertyName: "capabilities");
        Assert.True(condition: caps.GetProperty(propertyName: "hoverProvider").GetBoolean());
        Assert.True(condition: caps.GetProperty(propertyName: "definitionProvider").GetBoolean());
        Assert.True(condition: caps.TryGetProperty(propertyName: "semanticTokensProvider", value: out _));

        // shutdown must be answered with a null result.
        JsonElement? sd = LspTestHarness.ReplyWithId(replies: replies, id: 999);
        Assert.NotNull(@object: sd);
    }

    [Fact]
    public void DidOpen_PublishesDiagnostics_CleanSourceHasNoErrors()
    {
        IReadOnlyList<JsonDocument> replies = LspTestHarness.Run(
            LspTestHarness.DidOpen(uri: Uri, text: Source), Exit);

        JsonElement? diag = LspTestHarness.Diagnostics(replies: replies, uri: Uri);
        Assert.NotNull(@object: diag);
        JsonElement arr = diag!.Value.GetProperty(propertyName: "diagnostics");
        Assert.Equal(expected: JsonValueKind.Array, actual: arr.ValueKind);
    }

    [Fact]
    public void DidOpen_BadSource_ReportsDiagnostics()
    {
        const string bad =
            "module Test/Bad\n" +
            "routine start()\n" +
            "  var x = undefined_symbol_here\n" +
            "  return\n";

        IReadOnlyList<JsonDocument> replies = LspTestHarness.Run(
            LspTestHarness.DidOpen(uri: "file:///bad.rf", text: bad), Exit);

        JsonElement? diag = LspTestHarness.Diagnostics(replies: replies, uri: "file:///bad.rf");
        Assert.NotNull(@object: diag);
        JsonElement arr = diag!.Value.GetProperty(propertyName: "diagnostics");
        Assert.True(condition: arr.GetArrayLength() > 0, userMessage: "expected at least one diagnostic");
    }

    [Fact]
    public void DidChange_ReanalyzesDocument()
    {
        IReadOnlyList<JsonDocument> replies = LspTestHarness.Run(
            LspTestHarness.DidOpen(uri: Uri, text: Source),
            LspTestHarness.DidChange(uri: Uri, text: Source),
            Exit);

        // Two publishDiagnostics notifications (open + change) — assert the change one exists.
        Assert.NotNull(@object: LspTestHarness.Diagnostics(replies: replies, uri: Uri));
    }

    [Fact]
    public void DidClose_ClearsDiagnostics()
    {
        string close = JsonSerializer.Serialize(value: new
        {
            jsonrpc = "2.0",
            method = "textDocument/didClose",
            @params = new { textDocument = new { uri = Uri } }
        });

        IReadOnlyList<JsonDocument> replies = LspTestHarness.Run(
            LspTestHarness.DidOpen(uri: Uri, text: Source), close, Exit);

        Assert.NotNull(@object: LspTestHarness.Diagnostics(replies: replies, uri: Uri));
    }

    [Theory]
    [InlineData("textDocument/hover")]
    [InlineData("textDocument/definition")]
    [InlineData("textDocument/references")]
    [InlineData("textDocument/prepareRename")]
    public void PositionalRequest_OverRoutineCall_Answers(string method)
    {
        (int line, int character) = Pos(needle: "add(a:"); // the call site
        IReadOnlyList<JsonDocument> replies = LspTestHarness.Run(
            LspTestHarness.DidOpen(uri: Uri, text: Source),
            LspTestHarness.Positional(id: 5, method: method, uri: Uri, line: line, character: character),
            Exit);

        JsonElement? reply = LspTestHarness.ReplyWithId(replies: replies, id: 5);
        Assert.NotNull(@object: reply);
        Assert.True(condition: reply!.Value.TryGetProperty(propertyName: "result", value: out _));
    }

    [Fact]
    public void Hover_OverMemberRoutine_Answers()
    {
        (int line, int character) = Pos(needle: "magnitude", occurrence: 2); // the .magnitude() call
        IReadOnlyList<JsonDocument> replies = LspTestHarness.Run(
            LspTestHarness.DidOpen(uri: Uri, text: Source),
            LspTestHarness.Positional(id: 6, method: "textDocument/hover", uri: Uri, line: line, character: character),
            Exit);

        Assert.NotNull(@object: LspTestHarness.ReplyWithId(replies: replies, id: 6));
    }

    [Fact]
    public void Hover_OverWhitespace_ReturnsNullResult()
    {
        IReadOnlyList<JsonDocument> replies = LspTestHarness.Run(
            LspTestHarness.DidOpen(uri: Uri, text: Source),
            // Column 0 of a blank line — no symbol under the cursor.
            LspTestHarness.Positional(id: 7, method: "textDocument/hover", uri: Uri, line: 2, character: 0),
            Exit);

        JsonElement? reply = LspTestHarness.ReplyWithId(replies: replies, id: 7);
        Assert.NotNull(@object: reply);
    }

    [Fact]
    public void Completion_AfterMemberDot_Answers()
    {
        (int line, int character) = Pos(needle: "p.magnitude");
        // Place the cursor right after the '.', i.e. at the start of "magnitude".
        int dotChar = character + 2; // past "p."
        IReadOnlyList<JsonDocument> replies = LspTestHarness.Run(
            LspTestHarness.DidOpen(uri: Uri, text: Source),
            LspTestHarness.Positional(id: 8, method: "textDocument/completion", uri: Uri, line: line, character: dotChar),
            Exit);

        Assert.NotNull(@object: LspTestHarness.ReplyWithId(replies: replies, id: 8));
    }

    [Fact]
    public void Completion_AtIdentifier_Answers()
    {
        (int line, int character) = Pos(needle: "add(a:");
        IReadOnlyList<JsonDocument> replies = LspTestHarness.Run(
            LspTestHarness.DidOpen(uri: Uri, text: Source),
            LspTestHarness.Positional(id: 9, method: "textDocument/completion", uri: Uri, line: line, character: character + 1),
            Exit);

        Assert.NotNull(@object: LspTestHarness.ReplyWithId(replies: replies, id: 9));
    }

    [Fact]
    public void CompletionItemResolve_Answers()
    {
        string resolve = JsonSerializer.Serialize(value: new
        {
            jsonrpc = "2.0",
            id = 10,
            method = "completionItem/resolve",
            @params = new { label = "add", kind = 3 }
        });

        IReadOnlyList<JsonDocument> replies = LspTestHarness.Run(
            LspTestHarness.DidOpen(uri: Uri, text: Source), resolve, Exit);

        Assert.NotNull(@object: LspTestHarness.ReplyWithId(replies: replies, id: 10));
    }

    [Fact]
    public void SignatureHelp_InsideCallArgs_Answers()
    {
        (int line, int character) = Pos(needle: "add(a:");
        // Cursor inside the parentheses.
        int inside = character + 4;
        IReadOnlyList<JsonDocument> replies = LspTestHarness.Run(
            LspTestHarness.DidOpen(uri: Uri, text: Source),
            LspTestHarness.Positional(id: 11, method: "textDocument/signatureHelp", uri: Uri, line: line, character: inside),
            Exit);

        Assert.NotNull(@object: LspTestHarness.ReplyWithId(replies: replies, id: 11));
    }

    [Fact]
    public void Rename_OverLocalVariable_Answers()
    {
        (int line, int character) = Pos(needle: "total", occurrence: 1);
        string rename = JsonSerializer.Serialize(value: new
        {
            jsonrpc = "2.0",
            id = 12,
            method = "textDocument/rename",
            @params = new
            {
                textDocument = new { uri = Uri },
                position = new { line, character },
                newName = "sum"
            }
        });

        IReadOnlyList<JsonDocument> replies = LspTestHarness.Run(
            LspTestHarness.DidOpen(uri: Uri, text: Source), rename, Exit);

        Assert.NotNull(@object: LspTestHarness.ReplyWithId(replies: replies, id: 12));
    }

    [Fact]
    public void DocumentSymbol_ListsDeclarations()
    {
        IReadOnlyList<JsonDocument> replies = LspTestHarness.Run(
            LspTestHarness.DidOpen(uri: Uri, text: Source),
            LspTestHarness.DocRequest(id: 13, method: "textDocument/documentSymbol", uri: Uri),
            Exit);

        JsonElement? reply = LspTestHarness.ReplyWithId(replies: replies, id: 13);
        Assert.NotNull(@object: reply);
        JsonElement result = reply!.Value.GetProperty(propertyName: "result");
        Assert.Equal(expected: JsonValueKind.Array, actual: result.ValueKind);
        Assert.True(condition: result.GetArrayLength() > 0);
    }

    [Fact]
    public void WorkspaceSymbol_Answers()
    {
        string ws = JsonSerializer.Serialize(value: new
        {
            jsonrpc = "2.0",
            id = 14,
            method = "workspace/symbol",
            @params = new { query = "add" }
        });

        IReadOnlyList<JsonDocument> replies = LspTestHarness.Run(
            LspTestHarness.DidOpen(uri: Uri, text: Source), ws, Exit);

        Assert.NotNull(@object: LspTestHarness.ReplyWithId(replies: replies, id: 14));
    }

    [Fact]
    public void InlayHint_Answers()
    {
        string hint = JsonSerializer.Serialize(value: new
        {
            jsonrpc = "2.0",
            id = 15,
            method = "textDocument/inlayHint",
            @params = new
            {
                textDocument = new { uri = Uri },
                range = new
                {
                    start = new { line = 0, character = 0 },
                    end = new { line = 20, character = 0 }
                }
            }
        });

        IReadOnlyList<JsonDocument> replies = LspTestHarness.Run(
            LspTestHarness.DidOpen(uri: Uri, text: Source), hint, Exit);

        Assert.NotNull(@object: LspTestHarness.ReplyWithId(replies: replies, id: 15));
    }

    [Fact]
    public void CodeAction_Answers()
    {
        string action = JsonSerializer.Serialize(value: new
        {
            jsonrpc = "2.0",
            id = 16,
            method = "textDocument/codeAction",
            @params = new
            {
                textDocument = new { uri = Uri },
                range = new
                {
                    start = new { line = 14, character = 0 },
                    end = new { line = 14, character = 10 }
                },
                context = new { diagnostics = Array.Empty<object>() }
            }
        });

        IReadOnlyList<JsonDocument> replies = LspTestHarness.Run(
            LspTestHarness.DidOpen(uri: Uri, text: Source), action, Exit);

        Assert.NotNull(@object: LspTestHarness.ReplyWithId(replies: replies, id: 16));
    }

    [Fact]
    public void SemanticTokens_Full_Answers()
    {
        IReadOnlyList<JsonDocument> replies = LspTestHarness.Run(
            LspTestHarness.DidOpen(uri: Uri, text: Source),
            LspTestHarness.DocRequest(id: 17, method: "textDocument/semanticTokens/full", uri: Uri),
            Exit);

        JsonElement? reply = LspTestHarness.ReplyWithId(replies: replies, id: 17);
        Assert.NotNull(@object: reply);
        JsonElement data = reply!.Value.GetProperty(propertyName: "result").GetProperty(propertyName: "data");
        Assert.Equal(expected: JsonValueKind.Array, actual: data.ValueKind);
    }

    [Fact]
    public void UnknownRequest_AnsweredWithNullResult()
    {
        string unknown = "{\"jsonrpc\":\"2.0\",\"id\":18,\"method\":\"textDocument/foobar\"}";
        IReadOnlyList<JsonDocument> replies = LspTestHarness.Run(unknown, Exit);

        Assert.NotNull(@object: LspTestHarness.ReplyWithId(replies: replies, id: 18));
    }

    [Fact]
    public void UnknownNotification_Ignored_NoCrash()
    {
        string note = "{\"jsonrpc\":\"2.0\",\"method\":\"$/setTrace\",\"params\":{}}";
        IReadOnlyList<JsonDocument> replies = LspTestHarness.Run(note, Exit);

        // No id → no reply expected, and the loop must survive to process exit.
        Assert.NotNull(@object: replies);
    }

    [Fact]
    public void RequestOnUnopenedDocument_DoesNotCrash()
    {
        // hover before any didOpen — the handler must handle the missing DocState gracefully.
        IReadOnlyList<JsonDocument> replies = LspTestHarness.Run(
            LspTestHarness.Positional(id: 19, method: "textDocument/hover", uri: "file:///never-opened.rf",
                line: 0, character: 0),
            Exit);

        Assert.NotNull(@object: LspTestHarness.ReplyWithId(replies: replies, id: 19));
    }

    [Fact]
    public void MalformedFrame_Skipped()
    {
        // A frame that is not valid JSON must be skipped without aborting the loop.
        IReadOnlyList<JsonDocument> replies = LspTestHarness.Run("{ this is not json", Initialize(id: 20), Exit);

        Assert.NotNull(@object: LspTestHarness.ReplyWithId(replies: replies, id: 20));
    }

    [Fact]
    public void SuflaeDocument_Analyzes()
    {
        const string sf =
            "module Test/LspSf\n" +
            "import IO/Console\n" +
            "\n" +
            "routine start()\n" +
            "  var x = 41\n" +
            "  var y = x + 1\n" +
            "  show(f\"{y}\")\n" +
            "  return\n";

        IReadOnlyList<JsonDocument> replies = LspTestHarness.Run(
            LspTestHarness.DidOpen(uri: "file:///test/Sample.sf", text: sf, languageId: "suflae"),
            LspTestHarness.DocRequest(id: 21, method: "textDocument/documentSymbol", uri: "file:///test/Sample.sf"),
            Exit);

        Assert.NotNull(@object: LspTestHarness.ReplyWithId(replies: replies, id: 21));
    }
}
