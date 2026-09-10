using System.Text;
using System.Text.Json;
using Builder;
using Builder.Execution;

namespace RazorForge.Tests.Execution;

/// <summary>
/// In-process driver for <see cref="LspServer.Run(System.IO.Stream, System.IO.Stream)"/>. Builds a
/// stream of LSP-framed (<c>Content-Length</c>) JSON-RPC messages, runs the server loop over it, and
/// parses the framed replies back into <see cref="JsonDocument"/>s so tests can assert on the
/// server's real responses without spawning a process or touching the console.
/// </summary>
internal static class LspTestHarness
{
    /// <summary>Frames one JSON payload the way an LSP client would and appends it to <paramref name="sb"/>.</summary>
    private static void AppendFrame(MemoryStream stream, string json)
    {
        byte[] body = Encoding.UTF8.GetBytes(s: json);
        byte[] header = Encoding.ASCII.GetBytes(s: $"Content-Length: {body.Length}\r\n\r\n");
        stream.Write(buffer: header, offset: 0, count: header.Length);
        stream.Write(buffer: body, offset: 0, count: body.Length);
    }

    /// <summary>
    /// Runs the server over the given JSON-RPC messages (each a raw JSON object string) and returns
    /// every framed reply the server wrote, parsed. An <c>exit</c> notification is appended so the
    /// loop terminates deterministically.
    /// </summary>
    public static IReadOnlyList<JsonDocument> Run(params string[] messages)
    {
        using var input = new MemoryStream();
        foreach (string m in messages)
        {
            AppendFrame(stream: input, json: m);
        }

        input.Position = 0;
        using var output = new MemoryStream();

        LspServer.Run(stdin: input, stdout: output);

        return ParseFrames(bytes: output.ToArray());
    }

    /// <summary>Splits a byte buffer of concatenated <c>Content-Length</c> frames into parsed JSON documents.</summary>
    private static List<JsonDocument> ParseFrames(byte[] bytes)
    {
        var docs = new List<JsonDocument>();
        int pos = 0;
        while (pos < bytes.Length)
        {
            // Find the header/body separator (\r\n\r\n).
            int sep = IndexOf(haystack: bytes, needle: "\r\n\r\n"u8.ToArray(), start: pos);
            if (sep < 0)
            {
                break;
            }

            string headerBlock = Encoding.ASCII.GetString(bytes: bytes, index: pos, count: sep - pos);
            int contentLength = -1;
            foreach (string line in headerBlock.Split(separator: "\r\n"))
            {
                int colon = line.IndexOf(value: ':');
                if (colon > 0 &&
                    line[..colon].Trim().Equals(value: "Content-Length",
                        comparisonType: StringComparison.OrdinalIgnoreCase))
                {
                    _ = int.TryParse(s: line[(colon + 1)..].Trim(), result: out contentLength);
                }
            }

            int bodyStart = sep + 4;
            if (contentLength < 0 || bodyStart + contentLength > bytes.Length)
            {
                break;
            }

            docs.Add(item: JsonDocument.Parse(utf8Json: bytes.AsMemory(start: bodyStart, length: contentLength)));
            pos = bodyStart + contentLength;
        }

        return docs;
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int start)
    {
        for (int i = start; i <= haystack.Length - needle.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Zero-based (line, character) position of the <paramref name="occurrence"/>-th (1-based)
    /// occurrence of <paramref name="needle"/> in <paramref name="text"/>, as LSP positions. Text is
    /// assumed ASCII (UTF-16 code units == chars). Throws if the occurrence is not found.
    /// </summary>
    public static (int Line, int Character) PositionOf(string text, string needle, int occurrence = 1)
    {
        int idx = -1;
        for (int k = 0; k < occurrence; k++)
        {
            idx = text.IndexOf(value: needle, startIndex: idx + 1, comparisonType: StringComparison.Ordinal);
            if (idx < 0)
            {
                throw new ArgumentException(message: $"'{needle}' occurrence {occurrence} not found");
            }
        }

        int line = 0;
        int lineStart = 0;
        for (int i = 0; i < idx; i++)
        {
            if (text[i] == '\n')
            {
                line++;
                lineStart = i + 1;
            }
        }

        return (line, idx - lineStart);
    }

    /// <summary>JSON for a <c>textDocument/didOpen</c> notification.</summary>
    public static string DidOpen(string uri, string text, string languageId = "razorforge")
    {
        return JsonSerializer.Serialize(value: new
        {
            jsonrpc = "2.0",
            method = "textDocument/didOpen",
            @params = new
            {
                textDocument = new { uri, languageId, version = 1, text }
            }
        });
    }

    /// <summary>JSON for a <c>textDocument/didChange</c> notification carrying full text.</summary>
    public static string DidChange(string uri, string text, int version = 2)
    {
        return JsonSerializer.Serialize(value: new
        {
            jsonrpc = "2.0",
            method = "textDocument/didChange",
            @params = new
            {
                textDocument = new { uri, version },
                contentChanges = new object[] { new { text } }
            }
        });
    }

    /// <summary>JSON for a positional request (<c>hover</c>, <c>definition</c>, …) with a numeric id.</summary>
    public static string Positional(int id, string method, string uri, int line, int character)
    {
        return JsonSerializer.Serialize(value: new
        {
            jsonrpc = "2.0",
            id,
            method,
            @params = new
            {
                textDocument = new { uri },
                position = new { line, character }
            }
        });
    }

    /// <summary>JSON for a whole-document request (<c>documentSymbol</c>, <c>semanticTokens/full</c>, …).</summary>
    public static string DocRequest(int id, string method, string uri)
    {
        return JsonSerializer.Serialize(value: new
        {
            jsonrpc = "2.0",
            id,
            method,
            @params = new { textDocument = new { uri } }
        });
    }

    /// <summary>Returns the reply whose <c>id</c> equals <paramref name="id"/>, or null if none.</summary>
    public static JsonElement? ReplyWithId(IReadOnlyList<JsonDocument> replies, int id)
    {
        foreach (JsonDocument d in replies)
        {
            if (d.RootElement.TryGetProperty(propertyName: "id", value: out JsonElement idEl) &&
                idEl.ValueKind == JsonValueKind.Number && idEl.GetInt32() == id)
            {
                return d.RootElement;
            }
        }

        return null;
    }

    /// <summary>Returns the first <c>textDocument/publishDiagnostics</c> notification, or null.</summary>
    public static JsonElement? Diagnostics(IReadOnlyList<JsonDocument> replies, string uri)
    {
        foreach (JsonDocument d in replies)
        {
            if (d.RootElement.TryGetProperty(propertyName: "method", value: out JsonElement m) &&
                m.GetString() == "textDocument/publishDiagnostics" &&
                d.RootElement.TryGetProperty(propertyName: "params", value: out JsonElement p) &&
                p.TryGetProperty(propertyName: "uri", value: out JsonElement u) &&
                u.GetString() == uri)
            {
                return p;
            }
        }

        return null;
    }
}
